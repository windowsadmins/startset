using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Security;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// The ACL check: a file, or the folder holding it, that a non-administrator could write
/// is not trusted.
/// </summary>
public class FileTrustTests : IDisposable
{
    private const int FullControl = 0x1F01FF;
    private const int ReadAndExecute = 0x1200A9;
    private const int WriteData = 0x2;
    private const int GenericWrite = 0x40000000;
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string BuiltInAdministrator = "S-1-5-21-1111111111-2222222222-3333333333-500";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static AccessEntry[] LockedAcl =>
    [
        new(FileTrust.SystemSid, FullControl, Allow: true),
        new(FileTrust.AdministratorsSid, FullControl, Allow: true),
        new(FileTrust.UsersSid, ReadAndExecute, Allow: true)
    ];

    [Theory]
    [InlineData(FileTrust.SystemSid)]
    [InlineData(FileTrust.AdministratorsSid)]
    [InlineData(FileTrust.TrustedInstallerSid)]
    [InlineData(BuiltInAdministrator)]
    public void LockedAcl_WithAdministrativeOwner_IsTrusted(string owner)
    {
        FileTrust.Evaluate(owner, LockedAcl, "f").IsTrusted.Should().BeTrue();
    }

    [Fact]
    public void NonAdministratorOwner_IsNotTrusted()
    {
        var result = FileTrust.Evaluate(SomeUser, LockedAcl, "f");

        result.IsTrusted.Should().BeFalse();
        result.Reason.Should().Contain(SomeUser);
    }

    [Fact]
    public void UnknownOwner_IsNotTrusted()
    {
        FileTrust.Evaluate(null, LockedAcl, "f").IsTrusted.Should().BeFalse();
    }

    [Theory]
    [InlineData(FileTrust.UsersSid, WriteData)]
    [InlineData(AuthenticatedUsers, GenericWrite)]
    [InlineData(SomeUser, FullControl)]
    [InlineData(SomeUser, 0x10000)]  // delete
    [InlineData(SomeUser, 0x40000)]  // write DAC
    [InlineData(SomeUser, 0x80000)]  // write owner
    [InlineData(SomeUser, 0x40)]     // delete child
    [InlineData(SomeUser, 0x4)]      // append / add subfolder
    public void WriteRightForANonAdministrator_IsNotTrusted(string sid, int rights)
    {
        var acl = LockedAcl.Append(new AccessEntry(sid, rights, Allow: true));

        var result = FileTrust.Evaluate(FileTrust.SystemSid, acl, "f");

        result.IsTrusted.Should().BeFalse();
        result.Reason.Should().Contain(sid);
    }

    [Fact]
    public void ReadOnlyEntriesForUsers_AreTrusted()
    {
        var acl = LockedAcl.Append(new AccessEntry(SomeUser, ReadAndExecute, Allow: true));

        FileTrust.Evaluate(FileTrust.SystemSid, acl, "f").IsTrusted.Should().BeTrue();
    }

    [Fact]
    public void InheritOnlyEntries_DoNotApplyToTheObject()
    {
        var acl = LockedAcl.Append(new AccessEntry("S-1-3-0", FullControl, Allow: true, InheritOnly: true));

        FileTrust.Evaluate(FileTrust.SystemSid, acl, "f").IsTrusted.Should().BeTrue();
    }

    [Fact]
    public void DenyEntries_AreNotCreditedAgainstAnAllow()
    {
        var acl = LockedAcl
            .Append(new AccessEntry(FileTrust.UsersSid, WriteData, Allow: true))
            .Append(new AccessEntry(FileTrust.UsersSid, WriteData, Allow: false));

        FileTrust.Evaluate(FileTrust.SystemSid, acl, "f").IsTrusted.Should().BeFalse();
    }

    [Fact]
    public void RealFile_WritableByUsers_IsNotTrusted()
    {
        var path = _temp.CreateFile("payload.ps1", "Write-Output hi");
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, AccessControlType.Allow));
        file.SetAccessControl(security);

        FileTrust.CheckFile(path).IsTrusted.Should().BeFalse();
    }

    [Fact]
    public void RealFile_OwnedByTheTestUser_IsNotTrusted_UnlessElevated()
    {
        var path = _temp.CreateFile("payload.ps1", "Write-Output hi");
        var owner = FileTrust.OwnerOf(path);

        var result = FileTrust.CheckFile(path);

        if (!FileTrust.IsAdministrativeSid(owner))
            result.IsTrusted.Should().BeFalse();
    }

    [Fact]
    public void MissingFile_IsNotTrusted()
    {
        FileTrust.CheckFile(Path.Combine(_temp.Path, "nope.ps1")).IsTrusted.Should().BeFalse();
    }

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
    private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Theory]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.ondemand")]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.login")]
    public void UserContextTrigger_IsAcceptedFromAStandardUser(string path)
    {
        TriggerFiles.Evaluate(path, SomeUser).IsTrusted.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.ondemand-privileged")]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.login-privileged")]
    [InlineData(@"C:\ProgramData\ManagedState\triggers\.startset.cleanup")]
    public void PrivilegedTrigger_IsRefusedFromAStandardUser(string path)
    {
        TriggerFiles.Evaluate(path, SomeUser).IsTrusted.Should().BeFalse();
        TriggerFiles.Evaluate(path, FileTrust.AdministratorsSid).IsTrusted.Should().BeTrue();
    }

    [Fact]
    public void TriggersAreHonouredInBothFolders()
    {
        TriggerFiles.LocationsOf(@"C:\ProgramData\ManagedState\.startset.ondemand").Should().Equal(
            @"C:\ProgramData\ManagedState\.startset.ondemand",
            @"C:\ProgramData\ManagedState\triggers\.startset.ondemand");
    }
}
