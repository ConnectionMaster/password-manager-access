// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Newtonsoft.Json;
using PasswordManagerAccess.Common;

namespace PasswordManagerAccess.Kaspersky
{
    internal static class Client
    {
        public static void OpenVault(string username, string password, IRestTransport transport)
        {
            var rest = new RestClient(transport);

            // 1. Request login context token
            var loginContext = RequestLoginContext(rest);

            // 2. Login
            Login(username, password, loginContext, rest);

            // 3. Request user token
            var token = RequestUserToken(loginContext, rest);

            // 4. Finish login
            var authCookie = GetAuthCookie(token, rest);

            // 5. Get XMPP info
            var xmpp = GetXmppInfo(authCookie, rest);

            // 6. Generate JID
            var boshUrl = GetBoshUrl(xmpp, rest);

            var bosh = new Bosh(boshUrl);
            bosh.Connect("206a9e27-f96a-44d5-ac0d-84efe4f1835a#browser#5@39.ucp-ntfy.kaspersky-labs.com/portalsorucr8yj2l",
                         xmpp.XmppCredentials.Password,
                         transport);
        }

        //
        // Internal
        //

        internal static string RequestLoginContext(RestClient rest)
        {
            var response = rest.PostJson<R.Start>(
                "https://hq.uis.kaspersky.com/v3/logon/start",
                new Dictionary<string, object> {["Realm"] = "https://center.kaspersky.com/"});

            if (response.IsSuccessful)
                return response.Data.Context;

            throw MakeError(response);
        }

        // TODO: Handle and test invalid username and password
        internal static void Login(string username, string password, string loginContext, RestClient rest)
        {
            var response = rest.PostJson<R.Result>(
                "https://hq.uis.kaspersky.com/v3/logon/proceed",
                new Dictionary<string, object>
                {
                    ["login"] = username,
                    ["password"] = password,
                    ["logonContext"] = loginContext,
                    ["locale"] = "en",
                    ["captchaType"] = "invisible_recaptcha",
                    ["captchaAnswer"] = "undefined",
                });

            if (!response.IsSuccessful)
                throw MakeError(response);

            if (response.Data.Status == "Success")
                return;

            throw new InternalErrorException($"Unexpected response from {response.RequestUri}");
        }

        internal static string RequestUserToken(string loginContext, RestClient rest)
        {
            var response = rest.PostJson<R.UserToken>(
                "https://hq.uis.kaspersky.com/v3/logon/complete_active",
                new Dictionary<string, object>
                {
                    ["logonContext"] = loginContext,
                    ["TokenType"] = "SamlDeflate",
                    ["RememberMe"] = false,
                });

            if (!response.IsSuccessful)
                throw MakeError(response);

            return response.Data.Token;
        }

        internal static string GetAuthCookie(string userToken, RestClient rest)
        {
            var response = rest.PostJson(
                "https://my.kaspersky.com/SignIn/CompleteRestLogon",
                parameters: new Dictionary<string, object>
                {
                    ["samlDeflatedToken"] = userToken,
                    ["rememberMe"] = false,
                    ["resendActivationLink"] = false,
                },
                headers: new Dictionary<string, string>
                {
                    ["x-requested-with"] = "XMLHttpRequest"
                });

            if (!response.IsSuccessful)
                throw MakeError(response);

            var cookie = response.Cookies.GetOrDefault(AuthCookieName, "");
            if (cookie.IsNullOrEmpty())
                throw MakeError("Auth cookie not found");

            return cookie;
        }

        internal static R.XmppSettings GetXmppInfo(string authCookie, RestClient rest)
        {
            var response = rest.Get("https://my.kaspersky.com/MyPasswords",
                                    cookies: new Dictionary<string, string>{[AuthCookieName] = authCookie});
            if (!response.IsSuccessful)
                throw MakeError(response);

            var json = ExtractXmppSettings(response.Content);
            return ParseXmppSettings(json);
        }

        internal static string ExtractXmppSettings(string html)
        {
            const string xmppPrefix = "global.XmppSettings = _.extend(global.XmppSettings || {}, {";
            const string xmppSuffix = "});";

            var prefixIndex = html.IndexOf(xmppPrefix, StringComparison.Ordinal);
            if (prefixIndex < 0)
                throw new InternalErrorException("Failed to parse XMPP settings");

            var xmppStart = prefixIndex + xmppPrefix.Length - 1; // -1 to include the opening curly brace

            var suffixIndex = html.IndexOf(xmppSuffix, xmppStart, StringComparison.Ordinal);
            if (suffixIndex < 0)
                throw new InternalErrorException("Failed to parse XMPP settings");

            return html.Substring(xmppStart, suffixIndex - xmppStart + 1);
        }

        internal static R.XmppSettings ParseXmppSettings(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<R.XmppSettings>(json);
            }
            catch (JsonException e)
            {
                throw MakeError("Failed to parse XMPP settings", e);
            }
        }

        internal static string GetBoshUrl(R.XmppSettings xmpp, RestClient rest)
        {
            if (xmpp.XmppLibraryUrls.Length == 0)
                throw MakeError("The list of XMPP BOSH URLs returned by the server is empty");

            var userId = xmpp.UserId;
            var host = GetHost(xmpp.XmppLibraryUrls[0]);

            var notifyIndex = GetNotifyServerIndex(userId);
            var notifyServerGroup = GetNotifyServerGroup(host);
            var notifyHost = $"{notifyIndex}.{notifyServerGroup}";

            var jid = $"{userId}#browser#{ServiceId}@{notifyHost}/{JidResource}";
            var escapedJid = Uri.EscapeDataString(jid);

            var queryUrl = $"https://{host}/find_bosh_bind?uid={escapedJid}";

            var response = rest.Get(queryUrl);
            if (!response.IsSuccessful)
                throw MakeError(response);

            var boshUrl = response.Content;
            if (boshUrl.IsNullOrEmpty())
                throw MakeError("Failed to retrieve the BOSH bind URL");

            return boshUrl;
        }

        internal static string GetHost(string url)
        {
            return new Uri(url).Host;
        }

        internal static string GetNotifyServerIndex(string username)
        {
            // TODO: Implement this!
            return "39";
        }

        internal static string GetNotifyServerGroup(string host)
        {
            var dot = host.IndexOf('.');
            if (dot < 0)
                throw MakeError($"Expected '{host}' to have a subdomain");

            return host.Substring(dot + 1);
        }

        internal static BaseException MakeError(RestResponse<string> response)
        {
            // TODO: Make this more descriptive
            return MakeError($"Request to '{response.RequestUri}' failed");
        }

        internal static BaseException MakeError(string message, Exception inner = null)
        {
            return new InternalErrorException(message, inner);
        }

        // TODO: Move this out of here
        internal static class R
        {
            internal class Result
            {
                [JsonProperty("Status", Required = Required.Always)]
                public readonly string Status;
            }

            internal class Start: Result
            {
                [JsonProperty("LogonContext", Required = Required.Always)]
                public readonly string Context;
            }

            internal class UserToken
            {
                [JsonProperty("UserToken", Required = Required.Always)]
                public readonly string Token;

                [JsonProperty("TokenType", Required = Required.Always)]
                public readonly string Type;
            }

            internal class XmppSettings
            {
                [JsonProperty("userId", Required = Required.Always)]
                public readonly string UserId;

                [JsonProperty("pushNotificationEkaUniqueId", Required = Required.Always)]
                public readonly string PushNotificationEkaUniqueId;

                [JsonProperty("pushNotificationKpmServiceHasChangesUniqueId", Required = Required.Always)]
                public readonly string PushNotificationKpmServiceHasChangesUniqueId;

                [JsonProperty("commandResponseTimeout", Required = Required.Always)]
                public readonly int CommandResponseTimeout;

                [JsonProperty("commandLifetime", Required = Required.Always)]
                public readonly int CommandLifetime;

                [JsonProperty("xmppLibraryUrls", Required = Required.Always)]
                public readonly string[] XmppLibraryUrls;

                [JsonProperty("xmppCredentials", Required = Required.Always)]
                public readonly XmppCredentials XmppCredentials;
            }

            internal readonly struct XmppCredentials
            {
                [JsonProperty("userId", Required = Required.Always)]
                public readonly string UserId;

                [JsonProperty("password", Required = Required.Always)]
                public readonly string Password;

                [JsonProperty("dangerousPassword", Required = Required.Always)]
                public readonly string DangerousPassword;
            }
        }

        //
        // Data
        //

        internal const string AuthCookieName = "MyKFedAuth";
        internal const int ServiceId = 5;
        internal const string JidResource = "portalsorucr8yj2l"; // TODO: Make this random
    }
}
