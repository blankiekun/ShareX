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

using Newtonsoft.Json.Serialization;
using System;
using System.Reflection;

namespace ShareX.HelpersLib
{
    public class EncryptedStringValueProvider : IValueProvider
    {
        private const string encryptedTag = "$ShareXEncrypted$";
        // Written by Windows ShareX. It can't be decrypted here, so a settings folder copied across loads with these cleared.
        private const string windowsEncryptedTag = "$DPAPIEncrypted$";

        private PropertyInfo targetProperty;

        public EncryptedStringValueProvider(PropertyInfo targetProperty)
        {
            this.targetProperty = targetProperty;
        }

        // Saving. Throws if the value can't be encrypted, which aborts the save: plaintext is never written.
        public object GetValue(object target)
        {
            string value = (string)targetProperty.GetValue(target);

            if (string.IsNullOrEmpty(value) || value.StartsWith(encryptedTag, StringComparison.Ordinal))
            {
                return value;
            }

            return encryptedTag + SecretServiceCrypto.Encrypt(value);
        }

        // Loading. A value that won't decrypt stays in the property as its raw tagged string, so the next save
        // writes it back unchanged. Until then it would be sent as the credential, so say so in the log.
        public void SetValue(object target, object value)
        {
            string text = (string)value;

            if (!string.IsNullOrEmpty(text))
            {
                if (text.StartsWith(encryptedTag, StringComparison.Ordinal))
                {
                    try
                    {
                        text = SecretServiceCrypto.Decrypt(text.Substring(encryptedTag.Length));
                    }
                    catch (Exception e)
                    {
                        LogUnusableSecret(e.Message);
                    }
                }
                else if (text.StartsWith(windowsEncryptedTag, StringComparison.Ordinal))
                {
                    // Sending it would upload with a garbage credential, and no build here can ever read it.
                    DebugHelper.WriteLine($"Secret {targetProperty.DeclaringType.Name}.{targetProperty.Name} was encrypted by Windows ShareX and can't be read here. " +
                        "It was cleared, so sign in to that destination again.");
                    text = "";
                }
            }

            targetProperty.SetValue(target, text);
        }

        private void LogUnusableSecret(string reason)
        {
            DebugHelper.WriteLine($"Secret {targetProperty.DeclaringType.Name}.{targetProperty.Name} was not decrypted and is kept encrypted, " +
                $"so uploads using it will fail until it is re-entered or ShareX restarts with the wallet available: {reason}");
        }
    }
}