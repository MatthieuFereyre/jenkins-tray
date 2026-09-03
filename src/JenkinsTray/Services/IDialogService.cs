using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>Lets view-models open windows without referencing the views directly.</summary>
public interface IDialogService
{
    /// <summary>Shows the server editor. Returns true when the user confirmed.</summary>
    bool EditServer(ServerConfig config, bool isNew);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);
}
