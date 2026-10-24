using System.Diagnostics;

namespace Lingonberry.Vpn;

public static class WireGuardKeyGenerator
{
    /// <summary>
    /// Generates a WireGuard key pair by shelling out to the wg binary
    /// (wireguard-tools must be installed in the container image).
    /// </summary>
    public static (string PrivateKey, string PublicKey) Generate()
    {
        var privateKey = Run("wg", "genkey", stdin: null).Trim();
        var publicKey = Run("wg", "pubkey", stdin: privateKey).Trim();
        return (privateKey, publicKey);
    }

    private static string Run(string fileName, string arguments, string? stdin)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' could not be started. " +
                "Is wireguard-tools installed in the container image?", ex);
        }

        if (stdin != null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' failed: {error.Trim()}");
        }

        return output;
    }
}
