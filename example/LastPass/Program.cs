// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Threading;
using System.Threading.Tasks;
using PasswordManagerAccess.Common;
using PasswordManagerAccess.Example.Common;
using PasswordManagerAccess.LastPass;
using PasswordManagerAccess.LastPass.Ui;

namespace PasswordManagerAccess.Example.LastPass
{
    public static class Program
    {
        // Very simple text based user interface that demonstrates how to respond to
        // to Vault UI requests.
        private class TextUi : DuoAsyncUi, IAsyncUi
        {
            public Task<OtpResult> ProvideGoogleAuthPasscode(CancellationToken cancellationToken) =>
                ProvideOtpPasscode("Google Authenticator", cancellationToken);

            public Task<OtpResult> ProvideMicrosoftAuthPasscode(CancellationToken cancellationToken) =>
                ProvideOtpPasscode("Microsoft Authenticator", cancellationToken);

            public Task<OtpResult> ProvideYubikeyPasscode(CancellationToken cancellationToken) => ProvideOtpPasscode("Yubikey", cancellationToken);

            public Task<OobResult> ApproveLastPassAuth(CancellationToken cancellationToken) =>
                ApproveOutOfBand("LastPass Authenticator", cancellationToken);

            public Task<OobResult> ApproveDuo(CancellationToken cancellationToken) => ApproveOutOfBand("Duo Security", cancellationToken);

            public Task<OobResult> ApproveSalesforceAuth(CancellationToken cancellationToken) =>
                ApproveOutOfBand("Salesforce Authenticator", cancellationToken);

            //
            // Private
            //

            private static async Task<OtpResult> ProvideOtpPasscode(string method, CancellationToken cancellationToken)
            {
                var answer = await GetAnswer($"Please enter {method} code {PressEnterToCancel}", cancellationToken);
                return answer == "" ? OtpResult.Cancel : new OtpResult(answer, await GetRememberMe(cancellationToken));
            }

            private static async Task<OobResult> ApproveOutOfBand(string method, CancellationToken cancellationToken)
            {
                Console.WriteLine($"> Please approve out-of-band via {method} and press ENTER");
                var answer = await GetAnswer($"Or enter the {method} passcode from the app or 'c' to cancel", cancellationToken);

                if (answer.ToLower() == "c")
                    return OobResult.Cancel;

                var rememberMe = await GetRememberMe(cancellationToken);
                return answer.Length == 0 ? OobResult.WaitForApproval(rememberMe) : OobResult.ContinueWithPasscode(answer, rememberMe);
            }
        }

        public static async Task Main(string[] args)
        {
            var config = Util.ReadConfig();

            try
            {
                // Fetch and create the vault from LastPass
                var vault = await Vault.Open(
                    config["username"],
                    config["password"],
                    new ClientInfo(Platform.Desktop, config["client-id"], config["client-description"]),
                    new TextUi(),
                    new ParserOptions
                    {
                        // Set to true to parse "server" secure notes
                        ParseSecureNotesToAccount = false,
                        LoggingEnabled = true,
                    },
                    null,
                    CancellationToken.None
                );

                // Dump all the accounts
                for (var i = 0; i < vault.Accounts.Length; ++i)
                {
                    var account = vault.Accounts[i];
                    Console.WriteLine(
                        "{0}:\n"
                            + "        id: {1}\n"
                            + "      name: {2}\n"
                            + "  username: {3}\n"
                            + "  password: {4}\n"
                            + "       url: {5}\n"
                            + "      path: {6}\n"
                            + "     notes: {7}\n"
                            + "      totp: {8}\n"
                            + "  favorite: {9}\n"
                            + "    shared: {10}\n",
                        i + 1,
                        account.Id,
                        account.Name,
                        account.Username,
                        account.Password,
                        account.Url,
                        account.Path,
                        account.Notes,
                        account.Totp,
                        account.IsFavorite,
                        account.IsShared
                    );
                }
            }
            catch (BaseException e)
            {
                Util.PrintException(e);
            }
        }
    }
}
