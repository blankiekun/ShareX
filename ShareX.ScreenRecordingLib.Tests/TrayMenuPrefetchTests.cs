using System.Reflection;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class TrayMenuPrefetchTests
{
    // Catches: an Avalonia upgrade that renames or removes what DesktopTrayIconService.PrefetchSubmenus reflects on. The app would log once and go
    // back to submenus that often open empty on the first hover, so without this nobody would notice. Keep the names in step with that method.
    // Reads types only, so it needs no D-Bus session.
    [Fact]
    public void AvaloniaStillHasThePrivateMembersThePrefetchUses()
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assembly freeDesktop = Assembly.Load("Avalonia.FreeDesktop");
        Type? exporter = freeDesktop.GetType("Avalonia.FreeDesktop.DBusMenuExporter+DBusMenuExporterImpl");

        Assert.NotNull(exporter);
        Assert.True(typeof(Avalonia.Controls.Platform.ITopLevelNativeMenuExporter).IsAssignableFrom(exporter));
        Assert.Equal(typeof(int), exporter.GetMethod("GetId", Any, [typeof(Avalonia.Controls.NativeMenuItemBase)])?.ReturnType);
        Assert.Equal(typeof(uint), exporter.GetField("_revision", Any)?.FieldType);
        Assert.Equal(typeof(string), exporter.BaseType?.GetProperty("Path", Any)?.PropertyType);

        Type? connection = exporter.BaseType?.GetProperty("Connection", Any)?.PropertyType;
        Assert.NotNull(connection);
        MethodInfo? emit = freeDesktop.GetType("Avalonia.FreeDesktop.DBus.dbusmenuSignal")?.GetMethod("EmitLayoutUpdated");
        Assert.NotNull(emit);
        Assert.Equal([connection, typeof(Tmds.DBus.Protocol.ObjectPath), typeof(uint), typeof(int)], emit.GetParameters().Select(p => p.ParameterType));
    }
}
