using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Security;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// The trust rule: a file is trusted when its folder chain up to the data root is locked
/// and nobody but SYSTEM, Administrators or TrustedInstaller can write the file. The owner
/// SID is not part of it.
/// </summary>
public class FileTrustTests : IDisposable
{
    private const int FullControl = 0x1F01FF;
    private const int ReadAndExecute = 0x1200A9;
    private const int WriteData = 0x2;
    private const int GenericWrite = 0x40000000;
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string BuiltInAdministratorAccount = "S-1-5-21-1111111111-2222222222-3333333333-500";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        Unlock(_temp.Path);
        _temp.Dispose();
    }

    private static AccessEntry[] LockedAcl =>
    [
        new(FileTrust.SystemSid, FullControl, Allow: true),
        new(FileTrust.AdministratorsSid, FullControl, Allow: true),
        new(FileTrust.UsersSid, ReadAndExecute, Allow: true)
    ];

    // ── The pure decision ───────────────────────────────────────

    [Fact]
    public void LockedAcl_HasNoNonAdminWriter()
    {
        FileTrust.FindNonAdminWriter(LockedAcl).Should().BeNull();
    }

    [Theory]
    [InlineData(FileTrust.UsersSid, WriteData)]
    [InlineData(AuthenticatedUsers, GenericWrite)]
    [InlineData(SomeUser, FullControl)]
    [InlineData(SomeUser, 0x10000)]  // delete
    [InlineData(SomeUser, 0x40000)]  // WRITE_DAC
    [InlineData(SomeUser, 0x80000)]  // WRITE_OWNER
    [InlineData(SomeUser, 0x40)]     // delete child
    [InlineData(SomeUser, 0x4)]      // append / add subfolder
    [InlineData(BuiltInAdministratorAccount, WriteData)]  // only the three principals count
    public void WriteRightForAnyoneElse_IsFound(string sid, int rights)
    {
        FileTrust.FindNonAdminWriter(LockedAcl.Append(new AccessEntry(sid, rights, Allow: true))).Should().Be(sid);
    }

    [Fact]
    public void ReadOnlyEntries_AreNotWriters()
    {
        FileTrust.FindNonAdminWriter(LockedAcl.Append(new AccessEntry(SomeUser, ReadAndExecute, Allow: true))).Should().BeNull();
    }

    [Fact]
    public void InheritOnlyEntries_DoNotApplyToTheObject()
    {
        FileTrust.FindNonAdminWriter(LockedAcl.Append(new AccessEntry("S-1-3-0", FullControl, Allow: true, InheritOnly: true))).Should().BeNull();
    }

    [Fact]
    public void DenyEntries_AreNotCreditedAgainstAnAllow()
    {
        var acl = LockedAcl
            .Append(new AccessEntry(FileTrust.UsersSid, WriteData, Allow: true))
            .Append(new AccessEntry(FileTrust.UsersSid, WriteData, Allow: false));

        FileTrust.FindNonAdminWriter(acl).Should().Be(FileTrust.UsersSid);
    }

    // ── Real files and folders ──────────────────────────────────

    [Fact]
    public void FileInALockedChain_IsTrusted_WhoeverOwnsIt()
    {
        var file = _temp.CreateFile(@"login-every\setup.ps1", "Write-Output hi");
        Lock(_temp.Path);

        // Unelevated, the test account owns the file; elevated (as in CI) Administrators
        // does. Either way the owner is not part of the decision.
        FileTrust.CheckFile(file, _temp.Path, normalizeOwner: false).IsTrusted.Should().BeTrue();
    }

    [Fact]
    public void UnlockedRoot_IsNotTrusted()
    {
        var file = _temp.CreateFile(@"login-every\setup.ps1", "Write-Output hi");

        var result = FileTrust.CheckFile(file, _temp.Path, normalizeOwner: false);

        result.IsTrusted.Should().BeFalse();
        result.Reason.Should().Contain("is not locked");
    }

    [Fact]
    public void UnlockedPayloadFolder_IsNotTrusted()
    {
        var file = _temp.CreateFile(@"login-every\setup.ps1", "Write-Output hi");
        Lock(_temp.Path);
        Grant(Path.Combine(_temp.Path, "login-every"), WellKnownSidType.BuiltinUsersSid, FileSystemRights.CreateFiles);

        var result = FileTrust.CheckFile(file, _temp.Path, normalizeOwner: false);

        result.IsTrusted.Should().BeFalse();
        result.Reason.Should().Contain("login-every").And.Contain(FileTrust.UsersSid);
    }

    [Fact]
    public void FileWritableByUsers_IsNotTrusted_EvenInALockedChain()
    {
        var file = _temp.CreateFile(@"login-every\setup.ps1", "Write-Output hi");
        Lock(_temp.Path);
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
        info.SetAccessControl(security);

        var result = FileTrust.CheckFile(file, _temp.Path, normalizeOwner: false);

        result.IsTrusted.Should().BeFalse();
        result.Reason.Should().Contain("is writable by");
    }

    [Fact]
    public void MissingFile_IsNotTrusted()
    {
        FileTrust.CheckFile(Path.Combine(_temp.Path, "nope.ps1"), _temp.Path, normalizeOwner: false).IsTrusted.Should().BeFalse();
    }

    // ── The guard ───────────────────────────────────────────────

    [Fact]
    public void GuardAcl_MatchesTheInstallerSddl()
    {
        static string Dacl(string sddl) => sddl[sddl.IndexOf("D:", StringComparison.Ordinal)..];
        var notes = new List<string>();

        var root = DataDirectoryGuard.Locked(allowUserTriggers: false, notes)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var triggers = DataDirectoryGuard.Locked(allowUserTriggers: true, notes)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        Canonical(Dacl(root)).Should().Be(Canonical(Dacl(DataDirectoryGuard.RootSddl)));
        Canonical(Dacl(triggers)).Should().Be(Canonical(Dacl(DataDirectoryGuard.TriggerSddl)));
    }

    [Fact]
    public void FirstLock_QuarantinesFilesFromUnresolvedOwners_AndLocksTheTree()
    {
        _temp.CreateFile(@"login-every\from-a-user.ps1", "x");
        _temp.CreateFile("Config.yaml", "verbose: true");
        _temp.CreateFile(@"logs\2026-10-06\0900-boot\startset.log", "kept");

        var notes = DataDirectoryGuard.Secure(_temp.Path, isAdministrator: _ => false);

        var quarantine = Path.Combine(_temp.Path, DataDirectoryGuard.QuarantineFolderName);
        File.Exists(Path.Combine(_temp.Path, "login-every", "from-a-user.ps1")).Should().BeFalse();
        File.Exists(Path.Combine(_temp.Path, "Config.yaml")).Should().BeFalse();
        Directory.GetFiles(quarantine, "*", SearchOption.AllDirectories).Select(Path.GetFileName)
            .Should().BeEquivalentTo("from-a-user.ps1", "Config.yaml");
        notes.Should().Contain(n => n.StartsWith("Quarantined " + Path.Combine("login-every", "from-a-user.ps1")));

        // Logs are StartSet's own output and are never quarantined.
        File.Exists(Path.Combine(_temp.Path, @"logs\2026-10-06\0900-boot\startset.log")).Should().BeTrue();

        FileTrust.IsLocked(new DirectoryInfo(_temp.Path)).Should().BeTrue();
        FileTrust.IsLocked(new DirectoryInfo(Path.Combine(_temp.Path, "login-every"))).Should().BeTrue();
    }

    [Fact]
    public void FirstLock_KeepsFilesWhoseOwnerResolvesToAnAdministrator()
    {
        var file = _temp.CreateFile(@"login-every\from-an-admin.ps1", "x");

        DataDirectoryGuard.Secure(_temp.Path, isAdministrator: _ => true);

        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void LaterLocks_DoNotQuarantine()
    {
        var file = _temp.CreateFile(@"login-every\setup.ps1", "x");
        DataDirectoryGuard.Secure(_temp.Path, isAdministrator: _ => true);

        // The folder is locked now, so a file in it was written by an administrator.
        var notes = DataDirectoryGuard.Secure(_temp.Path, isAdministrator: _ => false);

        File.Exists(file).Should().BeTrue();
        notes.Should().NotContain(n => n.StartsWith("Quarantined"));
        FileTrust.CheckFile(file, _temp.Path, normalizeOwner: false).IsTrusted.Should().BeTrue();
    }

    // ── The engine ──────────────────────────────────────────────

    [Fact]
    public async Task Engine_SkipsAPayloadANonAdministratorCouldWrite()
    {
        var dir = Path.Combine(_temp.Path, "on-demand");
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(_temp.Path, "ran.txt");
        File.WriteAllText(Path.Combine(dir, "a.cmd"), $"@echo ran> \"{marker}\"");
        var engine = new ExecutionEngine(TestSettings.Service(), _temp.Path,
            _ => TrustResult.Untrusted("writable by S-1-5-32-545"));

        var results = await engine.ExecuteAsync([PayloadType.OnDemand], waitForNetwork: false);

        File.Exists(marker).Should().BeFalse();
        results.Should().ContainSingle().Which.Status.Should().Be(ExecutionStatus.Skipped);
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// SYSTEM and Administrators full control, Users read, not inherited -- the installed
    /// root ACL. The test account stays owner, so it can still undo this.
    /// </summary>
    private static void Lock(string root)
    {
        new DirectoryInfo(root).SetAccessControl(DataDirectoryGuard.Locked(allowUserTriggers: false, [], withOwner: false));
    }

    private static void Grant(string folder, WellKnownSidType sid, FileSystemRights rights)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Gives the test account full control again so the temp tree can be deleted.</summary>
    private static void Unlock(string root)
    {
        if (!Directory.Exists(root)) return;
        using var me = WindowsIdentity.GetCurrent();
        using var icacls = Process.Start(new ProcessStartInfo("icacls.exe")
        {
            ArgumentList = { root, "/grant", $"*{me.User!.Value}:(OI)(CI)F", "/T", "/C", "/Q" },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        icacls.WaitForExit();
    }

    /// <summary>
    /// Parses a DACL through the framework so ACE order, hex case and flag spelling do not
    /// make equal ACLs compare different.
    /// </summary>
    private static string Canonical(string dacl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
        return string.Join("|", security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => $"{r.IdentityReference.Value}:{(int)r.FileSystemRights:X}:{r.InheritanceFlags}:{r.PropagationFlags}:{r.AccessControlType}")
            .OrderBy(x => x, StringComparer.Ordinal))
            + $"|protected={security.AreAccessRulesProtected}";
    }
}

public class TriggerFilesTests
{
    [Theory]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.ondemand")]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.login")]
    [InlineData(@"C:\ProgramData\ManagedState\.startset.ondemand")]
    public void UserContextTrigger_IsHonouredInEitherFolder(string path)
    {
        TriggerFiles.Evaluate(path).IsTrusted.Should().BeTrue();
    }

    [Theory]
    [InlineData(".startset.ondemand-privileged")]
    [InlineData(".startset.login-privileged")]
    [InlineData(".startset.cleanup")]
    public void PrivilegedTrigger_IsHonouredOnlyInTheLockedRoot(string name)
    {
        TriggerFiles.Evaluate(Path.Combine(@"C:\ProgramData\ManagedState\triggers", name)).IsTrusted.Should().BeFalse();
        TriggerFiles.Evaluate(Path.Combine(@"C:\ProgramData\ManagedState", name)).IsTrusted.Should().BeTrue();
    }

    [Fact]
    public void TriggersAreHonouredInBothFolders()
    {
        TriggerFiles.LocationsOf(@"C:\ProgramData\ManagedState\.startset.ondemand").Should().Equal(
            @"C:\ProgramData\ManagedState\.startset.ondemand",
            @"C:\ProgramData\ManagedState\triggers\.startset.ondemand");
    }
}
