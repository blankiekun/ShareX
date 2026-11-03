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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    // Encrypts settings values with AES-GCM under one master key kept in the Secret Service.
    // D-Bus is only touched when there is a secret to process, so a config with no secrets never needs the wallet.
    public static class SecretServiceCrypto
    {
        private const string ServiceName = "org.freedesktop.secrets";
        private const string ServicePath = "/org/freedesktop/secrets";
        private const string ServiceInterface = "org.freedesktop.Secret.Service";
        private const string CollectionInterface = "org.freedesktop.Secret.Collection";
        private const string ItemInterface = "org.freedesktop.Secret.Item";
        private const string PromptInterface = "org.freedesktop.Secret.Prompt";
        private const string NoObject = "/";
        private const string KeyLabel = "ShareX settings encryption key";

        private const int KeySize = 32;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        // Long enough for a person to answer a wallet prompt.
        private static readonly TimeSpan ServiceTimeout = TimeSpan.FromMinutes(2);
        // One dismissed prompt or missing service shouldn't repeat for each of the secrets in a config.
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);

        // By ref: MessageWriter is a struct, and a copy's writes aren't seen by CreateMessage.
        private delegate void BodyWriter(ref MessageWriter writer);

        private static readonly KeyValuePair<string, string>[] KeyAttributes =
        {
            new("application", "ShareX"),
            new("type", "settings-master-key")
        };

        private static readonly object keyLock = new();
        private static byte[] cachedKey;
        private static Exception lastFailure;
        private static DateTime lastFailureTime;

        // Returns base64 of nonce + tag + ciphertext.
        public static string Encrypt(string plainText)
        {
            byte[] key = GetKey(true);
            byte[] plain = Encoding.UTF8.GetBytes(plainText);
            byte[] blob = new byte[NonceSize + TagSize + plain.Length];
            Span<byte> nonce = blob.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            using AesGcm aes = new(key, TagSize);
            aes.Encrypt(nonce, plain, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize));

            return Convert.ToBase64String(blob);
        }

        public static string Decrypt(string encrypted)
        {
            byte[] key = GetKey(false);
            byte[] blob = Convert.FromBase64String(encrypted);
            byte[] plain = new byte[blob.Length - NonceSize - TagSize];

            using AesGcm aes = new(key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);

            return Encoding.UTF8.GetString(plain);
        }

        private static byte[] GetKey(bool create)
        {
            lock (keyLock)
            {
                if (cachedKey != null)
                {
                    return cachedKey;
                }

                if (lastFailure != null && DateTime.UtcNow - lastFailureTime < FailureCooldown)
                {
                    throw new InvalidOperationException(lastFailure.Message, lastFailure);
                }

                try
                {
                    // Task.Run because callers may be on the UI thread, and this blocks until the service answers.
                    cachedKey = Task.Run(() => LoadKeyAsync(create)).WaitAsync(ServiceTimeout).GetAwaiter().GetResult();
                    lastFailure = null;
                }
                catch (Exception e)
                {
                    lastFailure = e;
                    lastFailureTime = DateTime.UtcNow;
                    throw;
                }

                return cachedKey ?? throw new InvalidOperationException("The Secret Service has no ShareX master key.");
            }
        }

        private static async Task<byte[]> LoadKeyAsync(bool create)
        {
            using DBusConnection connection = new(DBusAddress.Session);
            await connection.ConnectAsync();

            // shortcut: plain session, so the key crosses the session bus unencrypted. The peers that could read it there
            // run as this user and could equally ask the service for the key, so dh would add code without adding protection.
            string session = await connection.CallMethodAsync(
                CreateCall(connection, ServicePath, ServiceInterface, "OpenSession", "sv", (ref MessageWriter w) =>
                {
                    w.WriteString("plain");
                    w.WriteVariantString("");
                }),
                (message, _) =>
                {
                    Reader reader = message.GetBodyReader();
                    reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                }, null);

            string[] items = await SearchKeyItemsAsync(connection);
            string collection = NoObject;

            if (items.Length == 0)
            {
                // A service may hide the items of a locked collection, and creating a second key there would strand the first.
                collection = await connection.CallMethodAsync(
                    CreateCall(connection, ServicePath, ServiceInterface, "ReadAlias", "s", (ref MessageWriter w) => w.WriteString("default")),
                    (message, _) => message.GetBodyReader().ReadObjectPathAsString(), null);

                if (collection != NoObject)
                {
                    await UnlockAsync(connection, collection);
                    items = await SearchKeyItemsAsync(connection);
                }
            }

            if (items.Length == 0)
            {
                if (!create)
                {
                    return null;
                }

                if (collection == NoObject)
                {
                    throw new InvalidOperationException("The Secret Service has no default collection to keep the ShareX master key in.");
                }

                await CreateKeyItemAsync(connection, collection, session);

                // Read it back: never encrypt under a key the service didn't keep.
                items = await SearchKeyItemsAsync(connection);

                if (items.Length == 0)
                {
                    throw new InvalidOperationException("The Secret Service did not keep the ShareX master key.");
                }
            }

            // Two ShareX processes racing can create two keys. Ordering makes them settle on the same one.
            string item = items.OrderBy(path => path, StringComparer.Ordinal).First();
            await UnlockAsync(connection, item);

            string encodedKey = await connection.CallMethodAsync(
                CreateCall(connection, item, ItemInterface, "GetSecret", "o", (ref MessageWriter w) => w.WriteObjectPath(session)),
                (message, _) =>
                {
                    Reader reader = message.GetBodyReader();
                    reader.AlignStruct();
                    reader.ReadObjectPathAsSpan();
                    reader.ReadArrayOfByte();
                    return Encoding.ASCII.GetString(reader.ReadArrayOfByte());
                }, null);

            byte[] key = Convert.FromBase64String(encodedKey);

            // A damaged key is an error, not a reason to replace it: values encrypted under the real one would be stranded.
            return key.Length == KeySize ? key : throw new InvalidOperationException("The ShareX master key in the Secret Service has the wrong size.");
        }

        private static Task<string[]> SearchKeyItemsAsync(DBusConnection connection)
        {
            return connection.CallMethodAsync(
                CreateCall(connection, ServicePath, ServiceInterface, "SearchItems", "a{ss}", (ref MessageWriter w) =>
                {
                    ArrayStart attributes = w.WriteDictionaryStart();

                    foreach (KeyValuePair<string, string> attribute in KeyAttributes)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(attribute.Key);
                        w.WriteString(attribute.Value);
                    }

                    w.WriteDictionaryEnd(attributes);
                }),
                (message, _) =>
                {
                    Reader reader = message.GetBodyReader();
                    ObjectPath[] unlocked = reader.ReadArrayOfObjectPath();
                    ObjectPath[] locked = reader.ReadArrayOfObjectPath();
                    return unlocked.Concat(locked).Select(path => path.ToString()).ToArray();
                }, null);
        }

        private static async Task UnlockAsync(DBusConnection connection, string path)
        {
            string prompt = await connection.CallMethodAsync(
                CreateCall(connection, ServicePath, ServiceInterface, "Unlock", "ao", (ref MessageWriter w) => w.WriteArray(new ObjectPath[] { path })),
                (message, _) =>
                {
                    Reader reader = message.GetBodyReader();
                    reader.ReadArrayOfObjectPath();
                    return reader.ReadObjectPathAsString();
                }, null);

            await RunPromptAsync(connection, prompt);
        }

        private static async Task CreateKeyItemAsync(DBusConnection connection, string collection, string session)
        {
            string encodedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySize));

            string prompt = await connection.CallMethodAsync(
                CreateCall(connection, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b", (ref MessageWriter w) =>
                {
                    w.WriteDictionary(new Dictionary<string, VariantValue>
                    {
                        ["org.freedesktop.Secret.Item.Label"] = VariantValue.String(KeyLabel),
                        ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(KeyAttributes.ToDictionary(a => a.Key, a => a.Value)).AsVariantValue()
                    });
                    w.WriteStructureStart();
                    w.WriteObjectPath(session);
                    w.WriteArray(new byte[0]);
                    w.WriteArray(Encoding.ASCII.GetBytes(encodedKey));
                    w.WriteString("text/plain");
                    // Never replace: an existing key is what the values already on disk were encrypted with.
                    w.WriteBool(false);
                }),
                (message, _) =>
                {
                    Reader reader = message.GetBodyReader();
                    reader.ReadObjectPathAsSpan();
                    return reader.ReadObjectPathAsString();
                }, null);

            await RunPromptAsync(connection, prompt);
        }

        private static async Task RunPromptAsync(DBusConnection connection, string prompt)
        {
            if (prompt == NoObject)
            {
                return;
            }

            TaskCompletionSource<bool> dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using IDisposable watcher = await connection.WatchSignalAsync(ServiceName, prompt, PromptInterface, "Completed",
                (message, _) => message.GetBodyReader().ReadBool(),
                notification =>
                {
                    if (notification.HasValue)
                    {
                        dismissed.TrySetResult(notification.Value);
                    }
                    else
                    {
                        dismissed.TrySetException(notification.Exception ?? new InvalidOperationException("The Secret Service went away during a wallet prompt."));
                    }
                }, ObserverFlags.None, false, null);

            await connection.CallMethodAsync(CreateCall(connection, prompt, PromptInterface, "Prompt", "s", (ref MessageWriter w) => w.WriteString("")));

            if (await dismissed.Task)
            {
                throw new InvalidOperationException("The wallet prompt was dismissed.");
            }
        }

        private static MessageBuffer CreateCall(DBusConnection connection, string path, string @interface, string member, string signature, BodyWriter writeBody)
        {
            MessageWriter writer = connection.GetMessageWriter();

            try
            {
                writer.WriteMethodCallHeader(ServiceName, path, @interface, member, signature);
                writeBody(ref writer);
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }
    }
}
