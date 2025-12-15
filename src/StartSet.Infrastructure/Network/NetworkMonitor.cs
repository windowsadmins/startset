using System.Management;
using System.Net.NetworkInformation;
using StartSet.Infrastructure.Logging;

namespace StartSet.Infrastructure.Network;

/// <summary>
/// Monitors network connectivity for StartSet.
/// Uses NetworkInterface as primary method with WMI as fallback.
/// Matches outset's network wait behavior.
/// </summary>
public class NetworkMonitor
{
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates a network monitor with the specified timeout.
    /// </summary>
    /// <param name="timeoutSeconds">Timeout in seconds (default 180)</param>
    public NetworkMonitor(int timeoutSeconds = 180)
    {
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    /// <summary>
    /// Waits for network connectivity.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if network is available, false if timeout</returns>
    public async Task<bool> WaitForNetworkAsync(CancellationToken cancellationToken = default)
    {
        StartSetLogger.Information("Waiting for network connectivity (timeout: {Timeout}s)", _timeout.TotalSeconds);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < _timeout)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                StartSetLogger.Warning("Network wait cancelled");
                return false;
            }

            if (IsNetworkAvailable())
            {
                StartSetLogger.Information("Network connectivity established after {Elapsed:F1}s", stopwatch.Elapsed.TotalSeconds);
                return true;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        StartSetLogger.Warning("Network connectivity timeout after {Timeout}s", _timeout.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Checks if network is currently available.
    /// Uses NetworkInterface first, falls back to WMI.
    /// </summary>
    public bool IsNetworkAvailable()
    {
        // Primary check: NetworkInterface
        if (IsNetworkAvailableViaNetworkInterface())
            return true;

        // Fallback: WMI (more reliable in some enterprise scenarios)
        if (IsNetworkAvailableViaWmi())
            return true;

        return false;
    }

    /// <summary>
    /// Checks network availability using System.Net.NetworkInformation.
    /// </summary>
    private bool IsNetworkAvailableViaNetworkInterface()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                StartSetLogger.Debug("NetworkInterface reports no network available");
                return false;
            }

            // Check for active non-loopback interface with IP
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();

                // Check for gateway (indicates real network connectivity)
                if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    // Has IPv4 gateway - network is likely available
                    var ipv4Addresses = props.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString());

                    StartSetLogger.Debug("Network available via {InterfaceName}: {IpAddresses}",
                        ni.Name, string.Join(", ", ipv4Addresses));
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            StartSetLogger.Debug("NetworkInterface check failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Checks network availability using WMI.
    /// More reliable for detecting VPN and enterprise network configurations.
    /// </summary>
    private bool IsNetworkAvailableViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

            foreach (ManagementObject obj in searcher.Get())
            {
                var defaultGateway = obj["DefaultIPGateway"] as string[];
                if (defaultGateway != null && defaultGateway.Length > 0)
                {
                    var ipAddress = obj["IPAddress"] as string[];
                    var description = obj["Description"]?.ToString() ?? "Unknown";

                    StartSetLogger.Debug("WMI: Network available via {Adapter}: {Gateway}",
                        description, defaultGateway[0]);
                    return true;
                }
            }

            StartSetLogger.Debug("WMI: No network adapters with gateway found");
            return false;
        }
        catch (Exception ex)
        {
            StartSetLogger.Debug("WMI network check failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Gets detailed network status information.
    /// </summary>
    public NetworkStatus GetNetworkStatus()
    {
        var status = new NetworkStatus
        {
            CheckTime = DateTimeOffset.UtcNow,
            IsAvailable = IsNetworkAvailable()
        };

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                var info = new NetworkInterfaceInfo
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    Type = ni.NetworkInterfaceType.ToString(),
                    Status = ni.OperationalStatus.ToString(),
                    IpAddresses = props.UnicastAddresses
                        .Select(a => a.Address.ToString())
                        .ToList(),
                    Gateways = props.GatewayAddresses
                        .Select(g => g.Address.ToString())
                        .ToList()
                };

                status.Interfaces.Add(info);
            }
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }

        return status;
    }
}

/// <summary>
/// Network status information.
/// </summary>
public class NetworkStatus
{
    public DateTimeOffset CheckTime { get; set; }
    public bool IsAvailable { get; set; }
    public string? Error { get; set; }
    public List<NetworkInterfaceInfo> Interfaces { get; set; } = new();
}

/// <summary>
/// Information about a network interface.
/// </summary>
public class NetworkInterfaceInfo
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public List<string> IpAddresses { get; set; } = new();
    public List<string> Gateways { get; set; } = new();
}
