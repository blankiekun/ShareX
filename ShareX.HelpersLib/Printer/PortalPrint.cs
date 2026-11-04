#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable enable

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib;

/// <summary>
/// Prints through org.freedesktop.portal.Print: PreparePrint shows the desktop's dialog and returns the paper chosen,
/// then Print takes the PDF rendered for that paper, as an open file.
/// </summary>
public static class PortalPrint
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string PrintInterface = "org.freedesktop.portal.Print";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>US Letter, in hundredths of an inch.</summary>
    public static readonly Size DefaultPageSize = new(850, 1100);

    // Only a dead portal leaves a request unanswered, and the dialog can legitimately stay open a long time.
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// <paramref name="writePdf"/> gets the page size and returns the path of the PDF it wrote. The file is
    /// deleted as soon as it is open, so nothing stays behind in the temp folder whatever happens next.
    /// </summary>
    public static async Task<bool> PrintAsync(string parentWindow, string title, Func<Size, string> writePdf)
    {
        string sessionBus = DBusAddress.Session ?? throw new InvalidOperationException("There is no D-Bus session bus.");
        using DBusConnection connection = new(sessionBus);
        await connection.ConnectAsync();

        (uint response, Dictionary<string, VariantValue> results) = await RequestAsync(connection, "PreparePrint", "ssa{sv}a{sv}a{sv}",
            (ref MessageWriter writer, string token) =>
            {
                writer.WriteString(parentWindow);
                writer.WriteString(title);
                WriteEmptyDictionary(ref writer);
                WriteEmptyDictionary(ref writer);
                WriteOptions(ref writer, token, null);
            });
        if (!Succeeded(response)) return false;

        uint printToken = results["token"].GetUInt32();
        Size pageSize = DefaultPageSize;
        if (results.TryGetValue("page-setup", out VariantValue pageSetup))
        {
            Dictionary<string, VariantValue> setup = pageSetup.GetDictionary<string, VariantValue>();
            pageSize = PageSize(Number(setup, "Width"), Number(setup, "Height"),
                setup.TryGetValue("Orientation", out VariantValue orientation) ? orientation.GetString() : null);
        }

        // A handle of its own, opened after writing, starts at offset 0: the portal reads from wherever the handle is.
        string path = await Task.Run(() => writePdf(pageSize));
        SafeFileHandle pdf;
        try
        {
            pdf = File.OpenHandle(path);
        }
        finally
        {
            File.Delete(path);
        }

        using (pdf)
        {
            (response, _) = await RequestAsync(connection, "Print", "ssha{sv}",
                (ref MessageWriter writer, string token) =>
                {
                    writer.WriteString(parentWindow);
                    writer.WriteString(title);
                    writer.WriteHandle(pdf);
                    WriteOptions(ref writer, token, printToken);
                });
        }

        return Succeeded(response);
    }

    /// <summary>The portal's paper size, in millimetres, as hundredths of an inch; DefaultPageSize if it gave none.</summary>
    public static Size PageSize(double? widthMm, double? heightMm, string? orientation)
    {
        if (widthMm is not > 0 || heightMm is not > 0) return DefaultPageSize;

        int width = (int)Math.Round(widthMm.Value / 25.4 * 100), height = (int)Math.Round(heightMm.Value / 25.4 * 100);

        // Backends differ on whether Width and Height already follow the orientation.
        bool landscape = orientation?.Contains("landscape", StringComparison.OrdinalIgnoreCase) == true;
        return landscape && width < height ? new Size(height, width) : new Size(width, height);
    }

    private static double? Number(Dictionary<string, VariantValue> setup, string key) =>
        setup.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.Double ? value.GetDouble() : null;

    private static bool Succeeded(uint response) => response switch
    {
        0 => true,
        1 => false, // The user cancelled.
        _ => throw new IOException($"The print portal reported failure (response {response}).")
    };

    private delegate void WriteBody(ref MessageWriter writer, string token);

    // The portal answers with a Response signal on a path derived from our token. The subscription goes first, because
    // the answer can arrive before the call returns.
    private static async Task<(uint Response, Dictionary<string, VariantValue> Results)> RequestAsync(
        DBusConnection connection, string method, string signature, WriteBody writeBody)
    {
        string token = "sharex" + Guid.NewGuid().ToString("N");
        string sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        TaskCompletionSource<(uint, Dictionary<string, VariantValue>)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable subscription = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = $"{ObjectPath}/request/{sender}/{token}", Interface = RequestInterface, Member = "Response" },
            static (Message message, object? _) =>
            {
                Reader reader = message.GetBodyReader();
                uint response = reader.ReadUInt32();
                return (response, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Notification<(uint, Dictionary<string, VariantValue>)> notification) =>
            {
                if (notification.HasValue) answer.TrySetResult(notification.Value);
                else if (notification.Exception is Exception exception) answer.TrySetException(exception);
            },
            false);

        MessageBuffer call;
        MessageWriter writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, PrintInterface, method, signature);
            writeBody(ref writer, token);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        await connection.CallMethodAsync(call);
        return await answer.Task.WaitAsync(ResponseTimeout);
    }

    private static void WriteOptions(ref MessageWriter writer, string token, uint? printToken)
    {
        ArrayStart options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("modal");
        writer.WriteVariantBool(true);
        if (printToken is uint value)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("token");
            writer.WriteVariantUInt32(value);
        }
        writer.WriteDictionaryEnd(options);
    }

    private static void WriteEmptyDictionary(ref MessageWriter writer) => writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
}
