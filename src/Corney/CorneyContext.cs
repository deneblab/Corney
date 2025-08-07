using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using Corney.Features.App;
using Corney.Properties;
using Microsoft.Extensions.Logging;

namespace Corney;

public class CorneyContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ILogger<CorneyContext> _logger;

    public CorneyContext(ILogger<CorneyContext> log, CorneyRegistry registry,
        IConfigFileMenuService configFileMenuService)
    {
        _logger = log;
        
        var exitMenuItem = new ToolStripMenuItem("Exit", null, OnExit);
        var aboutMenuItem = new ToolStripMenuItem($"Corney - {registry.AppVersion.Sem} (Administrator: {PrivilegeHelper.IsRunAsAdministrator()})");
    var icon = PrivilegeHelper.IsRunAsAdministrator() 
            ? Resources.ResourceManager.GetObject("clock_red") as Icon
            : Resources.ResourceManager.GetObject("clock_green") as Icon;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            ContextMenuStrip = new ContextMenuStrip(),
            Text = $@"Corney - {registry.AppVersion.Sem}",
            Visible = true
        };

        _notifyIcon.ContextMenuStrip.Items.Add(aboutMenuItem);
   
        
        if (!PrivilegeHelper.IsRunAsAdministrator())
        {
            var runAsAdminMenuItem = new ToolStripMenuItem("Run as Administrator", null, OnRunAsAdmin);
            _notifyIcon.ContextMenuStrip.Items.Add(runAsAdminMenuItem);
        }
        
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(configFileMenuService.CreateConfigFilesMenu(registry));
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);
        _notifyIcon.DoubleClick += (s, e) => MessageBox.Show("App is running in tray.");
    }

    private void OnExit(object sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    private void OnRunAsAdmin(object sender, EventArgs e)
    {
        try
        {
            var executablePath = Environment.ProcessPath!;
            

            var processInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            _logger.LogInformation("Attempting to restart application with elevated privileges: {ExecutablePath}", executablePath);
            
            Process.Start(processInfo);
            
            _notifyIcon.Visible = false;
            Application.Exit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart application with elevated privileges");
            MessageBox.Show("Failed to restart as administrator. " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

public static class PrivilegeHelper
{
    public static bool IsRunAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}