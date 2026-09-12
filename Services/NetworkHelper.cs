using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace boston_timing_system.Services
{
    public static class NetworkHelper
    {
        public static string GetLocalIpAddress()
        {
            try
            {
                // Prioritize active network interfaces (Ethernet / Wi-Fi) with operational status Up
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                        networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    var ipProperties = networkInterface.GetIPProperties();
                    foreach (var address in ipProperties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(address.Address))
                        {
                            return address.Address.ToString();
                        }
                    }
                }

                // Fallback to DNS hostname resolution
                var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                var ipv4Address = hostEntry.AddressList.FirstOrDefault(
                    ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));

                return ipv4Address?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }
}
