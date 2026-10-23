using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Player.App.Platform;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private void InitializeExplorerSettings()
    {
        AddExplorerAction("Open with mpv.win", "Add this copy of mpv.win to the Open with menu for videos. This takes effect immediately for your Windows account.",
            "Register", ExplorerIntegration.Register, "mpv.win is registered. Use Open with in Explorer or choose it in Windows Default apps.");
        AddExplorerAction("Default video player", "Go straight to mpv.win's video file types in Windows settings. This adds mpv.win to Open with, then lets you choose which types it opens by default.",
            "Choose defaults", ExplorerIntegration.OpenDefaultApps, null);
    }
    private void AddExplorerAction(string title, string description, string action, Action invoke, string? success)
    {
        var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) =>
        {
            try { invoke(); if (success is not null) ShowNotice(success, InfoBarSeverity.Success); }
            catch (Exception ex) { ShowNotice("Couldn't complete the Explorer action. " + ex.Message, InfoBarSeverity.Error); }
        };
        AddRow(GeneralFields, title, description, button);
    }
}
