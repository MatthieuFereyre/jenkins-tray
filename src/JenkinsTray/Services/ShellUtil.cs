using System.Diagnostics;

namespace JenkinsTray.Services;

public static class ShellUtil
{
    /// <summary>Opens a URL in the user's default browser. Only http(s) is accepted.</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser registered — nothing we can do.
        }
    }

    public static void OpenUrl(Uri? uri) => OpenUrl(uri?.AbsoluteUri);
}
