using System.Windows;
using JenkinsTray.Models;
using Wpf.Ui.Appearance;

namespace JenkinsTray.Services;

public static class ThemeService
{
    private static Window? _trackedWindow;
    private static bool _isWatching;
    private static AppTheme _current = AppTheme.System;

    /// <summary>
    /// Registers the window used to follow the OS theme. The watcher can only be attached to a
    /// loaded window, so tracking is deferred until then — which also covers a start in the tray.
    /// </summary>
    public static void Track(Window window)
    {
        _trackedWindow = window;
        _isWatching = false;

        if (window.IsLoaded)
            Apply(_current);
        else
            window.Loaded += OnWindowLoaded;
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            window.Loaded -= OnWindowLoaded;

        Apply(_current);
    }

    public static void Apply(AppTheme theme)
    {
        _current = theme;

        // Registered before the theme is applied so the very first change is not missed.
        CardPaletteService.Start();

        StopWatching();

        switch (theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;

            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;

            default:
                ApplicationThemeManager.ApplySystemTheme();
                StartWatching();
                break;
        }
    }

    /// <summary>The watcher would override an explicit Light/Dark choice on the next OS theme flip.</summary>
    private static void StopWatching()
    {
        if (!_isWatching || _trackedWindow is not { IsLoaded: true } window)
        {
            _isWatching = false;
            return;
        }

        try
        {
            SystemThemeWatcher.UnWatch(window);
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("Stopping the system theme watcher", ex);
        }

        _isWatching = false;
    }

    private static void StartWatching()
    {
        if (_trackedWindow is not { IsLoaded: true } window)
            return;

        try
        {
            SystemThemeWatcher.Watch(window);
            _isWatching = true;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("Starting the system theme watcher", ex);
        }
    }
}
