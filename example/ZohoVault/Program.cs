// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using PasswordManagerAccess.Common;
using PasswordManagerAccess.Example.Common;
using PasswordManagerAccess.ZohoVault;
using PasswordManagerAccess.ZohoVault.Ui;

namespace PasswordManagerAccess.Example.ZohoVault
{
    class TextUi : BaseUi, IUi
    {
        private readonly string _totpSecret;

        public TextUi(string totpSecret)
        {
            _totpSecret = totpSecret;
        }

        public Passcode ProvideGoogleAuthPasscode()
        {
            if (string.IsNullOrEmpty(_totpSecret))
                return ProvideOtpPasscode("Google Authenticator");

            var totp = Util.CalculateGoogleAuthTotp(_totpSecret);
            Console.WriteLine($"Auto-generated TOTP: {totp}");
            Console.WriteLine("Remember this device: no");

            return new Passcode(totp, false);
        }

        //
        // Private
        //

        private static Passcode ProvideOtpPasscode(string method)
        {
            var answer = GetAnswer($"Please enter {method} code {PressEnterToCancel}");
            return answer == "" ? Passcode.Cancel : new Passcode(answer, GetRememberMe());
        }
    }

    static class Program
    {
        static void DumpAccount(Account account, string index)
        {
            Console.WriteLine(
                "{0}:\n"
                    + "          id: {1}\n"
                    + "        name: {2}\n"
                    + "    username: {3}\n"
                    + "    password: {4}\n"
                    + "         url: {5}\n"
                    + "        note: {6}\n",
                index,
                account.Id,
                account.Name,
                account.Username,
                account.Password,
                account.Url,
                account.Note
            );
        }

        static void Main(string[] args)
        {
            var config = Util.ReadConfig();
            string totpSecret;
            config.TryGetValue("google-auth-totp-secret", out totpSecret);

            var credentials = new Credentials(Username: config["username"], Password: config["password"], Passphrase: config["passphrase"]);
            var ui = new TextUi(totpSecret);
            var storage = new PlainStorage();

            Session session = null;
            try
            {
                // Stage 1: LogIn
                session = Client.LogIn(credentials, ui, storage);

                // Enable this to get a single item from the vault
                var getSingleItem = true;

                if (getSingleItem)
                {
                    // Stage 2a: Get a single item from the vault by ID
                    // Replace with an actual item ID from your vault
                    var account = Client.GetItem("34896000000017025", session);

                    account.Switch(a => DumpAccount(a, "Single item retrieved"), no => Console.WriteLine($"No item: {no}"));
                }
                else
                {
                    // Stage 2b: Download entire vault
                    var vault = Client.DownloadVault(session);

                    // Print the decrypted accounts
                    for (var i = 0; i < vault.Accounts.Length; ++i)
                        DumpAccount(vault.Accounts[i], (i + 1).ToString());
                }
            }
            catch (BaseException e)
            {
                Util.PrintException(e);
            }
            finally
            {
                // Stage 3: LogOut
                if (session != null)
                {
                    // Enable this to keep the session cookies in the secure storage and not log out.
                    // This allows the subsequent logins to reuse the previous session without doing a full login.
                    // Zoho limits the number of full logins to 20 per day.
                    var keepSession = true;

                    // Either LogOut or Dispose is required to clean up the session.
                    if (keepSession)
                        session.Dispose();
                    else
                        Client.LogOut(session, storage);
                }
            }
        }
    }
}
