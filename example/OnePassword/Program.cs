// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using PasswordManagerAccess.Common;
using PasswordManagerAccess.Example.Common;
using PasswordManagerAccess.OnePassword;
using PasswordManagerAccess.OnePassword.Ui;

namespace Example
{
    public static class Program
    {
        private class TextUi: DuoUi, IUi
        {
            public Passcode ProvideGoogleAuthPasscode()
            {
                var passcode = GetAnswer($"Enter Google Authenticator passcode {PressEnterToCancel}");
                return string.IsNullOrWhiteSpace(passcode)
                    ? Passcode.Cancel
                    : new Passcode(passcode, GetRememberMe());
            }

            public Passcode ProvideWebAuthnRememberMe()
            {
                var yesNo = GetAnswer($"Remember this device? {PressEnterToCancel}").ToLower();
                return string.IsNullOrWhiteSpace(yesNo)
                    ? Passcode.Cancel
                    : new Passcode("", yesNo == "y" || yesNo == "yes");
            }
        }

        public static void Main()
        {
            // Read 1Password credentials from a file
            // The file should contain 5 values:
            //   - username
            //   - password
            //   - account key
            //   - client UUID or device ID
            //   - API domain (my.1password.com, my.1password.eu or my.1password.ca)
            // See config.yaml.example for an example.
            var config = Util.ReadConfig();

            try
            {
                DumpAllVaults(config["username"],
                              config["password"],
                              config["account-key"],
                              config["device-id"],
                              config["domain"]);
            }
            catch (BaseException e)
            {
                Util.PrintException(e);
            }
        }

        private static void DumpAllVaults(string username,
                                          string password,
                                          string accountKey,
                                          string uuid,
                                          string domain)
        {
            var clientInfo = new ClientInfo
            {
                Username = username,
                Password = password,
                AccountKey = accountKey,
                Uuid = uuid,
                Domain = string.IsNullOrWhiteSpace(domain) ? Region.Global.ToDomain() : domain,
                DeviceName = "PMA 1Password example",
                DeviceModel = "1.0.0",
            };

            var session = Client.LogIn(clientInfo, new TextUi(), new PlainStorage());

            try
            {
                var vaults = Client.ListAllVaults(session);

                for (var i = 0; i < vaults.Length; i++)
                    DumpVault(i, vaults[i], session);
            }
            finally
            {
                Client.LogOut(session);
            }
        }

        private static void DumpVault(int index, VaultInfo vaultInfo, Session session)
        {
            Console.WriteLine("{0}: '{1}', '{2}':", index + 1, vaultInfo.Id, vaultInfo.Name);

            var vault = Client.OpenVault(vaultInfo, session);
            for (var i = 0; i < vault.Accounts.Length; ++i)
            {
                var account = vault.Accounts[i];
                Console.WriteLine("  {0}:\n" +
                                  "          id: {1}\n" +
                                  "        name: {2}\n" +
                                  "    username: {3}\n" +
                                  "    password: {4}\n" +
                                  "         url: {5}\n" +
                                  "        note: {6}\n",
                                  i + 1,
                                  account.Id,
                                  account.Name,
                                  account.Username,
                                  account.Password,
                                  account.MainUrl,
                                  account.Note);
            }
        }
    }
}
