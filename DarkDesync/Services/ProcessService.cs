using System.Diagnostics;
using System.Runtime.InteropServices;
using DarkDesync.Models;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Services;

/// <summary>
/// Enumerates running processes and their windows for the process selector.
/// </summary>
public sealed class ProcessService
{
    private readonly ILogger<ProcessService> _logger;

    public ProcessService(ILogger<ProcessService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets all processes that have visible windows (likely user applications).
    /// </summary>
    public List<ProcessInfo> GetWindowedProcesses()
    {
        var result = new List<ProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero)
                        continue;

                    string windowTitle = proc.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(windowTitle))
                        continue;

                    // Skip system processes
                    string name = proc.ProcessName.ToLowerInvariant();
                    if (name is "explorer" or "shellexperiencehost" or "searchhost"
                        or "startmenuexperiencehost" or "textinputhost" or "applicationframehost")
                        continue;

                    string fileName = string.Empty;
                    try
                    {
                        fileName = proc.MainModule?.FileName ?? string.Empty;
                    }
                    catch
                    {
                        // Access denied for some processes
                    }

                    result.Add(new ProcessInfo
                    {
                        ProcessId = (uint)proc.Id,
                        ProcessName = proc.ProcessName,
                        WindowTitle = windowTitle,
                        FileName = fileName,
                        MainWindowHandle = proc.MainWindowHandle,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Skipping process {Pid}", proc.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate processes");
        }

        return result.OrderBy(p => p.ProcessName).ToList();
    }

    /// <summary>
    /// Gets all processes (including those without windows).
    /// </summary>
    public List<ProcessInfo> GetAllProcesses()
    {
        var result = new List<ProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    string fileName = string.Empty;
                    try { fileName = proc.MainModule?.FileName ?? string.Empty; }
                    catch { }

                    result.Add(new ProcessInfo
                    {
                        ProcessId = (uint)proc.Id,
                        ProcessName = proc.ProcessName,
                        WindowTitle = proc.MainWindowTitle,
                        FileName = fileName,
                        MainWindowHandle = proc.MainWindowHandle,
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate processes");
        }

        return result.OrderBy(p => p.ProcessName).ToList();
    }

    /// <summary>
    /// Checks if a process is still running.
    /// </summary>
    public bool IsProcessRunning(uint processId)
    {
        try
        {
            var proc = Process.GetProcessById((int)processId);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
