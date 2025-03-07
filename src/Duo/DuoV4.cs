// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;
using PasswordManagerAccess.Common;
using static PasswordManagerAccess.Duo.Util;
using R = PasswordManagerAccess.Duo.ResponseV4;

namespace PasswordManagerAccess.Duo
{
    internal static class DuoV4
    {
        public static Result Authenticate(string authUrl, IDuoUi ui, IRestTransport transport)
        {
            var rest = new RestClient(transport);

            // 1. First get the main page
            var (html, url, cookies) = GetMainHtml(authUrl, rest);

            // 2. The main page contains the form that we need to POST to
            string host;
            (host, cookies) = SubmitSystemProperties(html, url, cookies, rest);

            // 3. Get `sid`
            var sessionId = ExtractSessionId(url);

            // 4. Extract `xsrf` token. It's used in some requests.
            var xsrf = ExtractXsrf(html);

            // 5. New rest with the API host
            var apiRest = new RestClient(transport,
                                         $"https://{host}/frame/v4/",
                                         defaultHeaders: new Dictionary<string, string>
                                         {
                                             ["X-Xsrftoken"] = xsrf,
                                         },
                                         defaultCookies: cookies);

            // 6. Get available devices and their methods
            var devices = GetDevices(sessionId, apiRest);

            while (true)
            {
                // Ask the user to choose what to do
                var choice = ui.ChooseDuoFactor(devices);
                if (choice == null)
                    return null; // Canceled by user

                // SMS is a special case: it doesn't submit any codes, it rather tells the server to send a new code
                // to the phone via SMS.
                if (choice.Factor == DuoFactor.SendPasscodesBySms)
                {
                    _ = SubmitFactor(sessionId, choice, "", apiRest);
                    choice = new DuoChoice(choice.Device, DuoFactor.Passcode, choice.RememberMe);
                }

                // Ask for the passcode
                var passcode = "";
                if (choice.Factor == DuoFactor.Passcode)
                {
                    passcode = ui.ProvideDuoPasscode(choice.Device);
                    if (passcode.IsNullOrEmpty())
                        return null; // Canceled by user
                }

                var token = SubmitFactorAndWaitForToken(sessionId, xsrf, choice, passcode, ui, apiRest);

                // Flow error like an incorrect passcode. The UI has been updated with the error. Keep going.
                if (token.IsNullOrEmpty())
                    continue;

                // All good
                return new Result(token, choice.RememberMe);
            }
        }

        //
        // Internal
        //

        internal static (HtmlDocument Html, string RedirectUrl, Dictionary<string, string> Cookies) GetMainHtml(
            string authUrl,
            RestClient rest)
        {
            var response = rest.Get(authUrl);
            if (!response.IsSuccessful)
                throw MakeSpecializedError(response);

            return (Parse(response.Content), response.RequestUri.AbsoluteUri, response.Cookies);
        }

        internal static (string Host, Dictionary<string, string> Cookies) SubmitSystemProperties(
            HtmlDocument html,
            string url,
            Dictionary<string, string> cookies,
            RestClient rest)
        {
            // Find the main form
            var form = html.DocumentNode.SelectSingleNode("//form[@id='plugin_form']");
            if (form == null)
                throw MakeInvalidResponseError("Duo HTML: the main form is not found");

            // Assign the known values. There are might be others defaulted to "". We're going to ignore them as long
            // as it doesn't break the process.
            var properties = new Dictionary<string, object>();
            foreach (var input in form.SelectNodes("./input"))
            {
                var name = input.GetAttributeValue("name", "");
                if (name.IsNullOrEmpty())
                    continue;

                var value = input.GetAttributeValue("value", "");
                if (value.IsNullOrEmpty())
                    value = KnownSystemProperties.GetOrDefault(name, "");

                properties[name] = value;
            }

            var response = rest.PostForm(url, properties, cookies: cookies);
            if (!response.IsSuccessful)
                throw MakeSpecializedError(response);

            return (response.RequestUri.Host, response.Cookies);
        }

        internal static string ExtractSessionId(string url)
        {
            return ExtractQueryParameter(url, "sid") ??
                   throw MakeInvalidResponseError("failed to find the session ID parameter in the URL");
        }

        internal static string ExtractXsrf(HtmlDocument html)
        {
            var xsrf = html.DocumentNode
                .SelectSingleNode("//form[@id='plugin_form']/input[@name='_xsrf']")?
                .GetAttributeValue("value", "");

            if (xsrf.IsNullOrEmpty())
                throw MakeInvalidResponseError("failed to find the 'xsrf' token");

            return xsrf;
        }

        internal static DuoDevice[] GetDevices(string sessionId, RestClient rest)
        {
            var response = rest.Get<Response.Envelope<R.Data>>($"auth/prompt/data?post_auth_action=OIDC_EXIT&sid={sessionId}");
            if (!response.IsSuccessful)
                throw MakeSpecializedError(response);

            return ParseDeviceData(response.Data.Payload);
        }

        internal static DuoDevice[] ParseDeviceData(R.Data data)
        {
            return data.Phones
                .Select(x => new DuoDevice(id: x.Id, name: x.Name, GetDeviceFactors(x.Key, data.Methods)))
                .ToArray();
        }

        internal static DuoFactor[] GetDeviceFactors(string key, R.Method[] methods)
        {
            return methods
                .Where(x => x.DeviceKey == key || x.DeviceKey.IsNullOrEmpty())
                .Where(x => StringToFactor.ContainsKey(x.Factor))
                .Select(x => StringToFactor[x.Factor])
                .ToArray();
        }

        internal static string SubmitFactor(string sid, DuoChoice choice, string passcode, RestClient rest)
        {
            var parameters = new Dictionary<string, object>
            {
                ["sid"] = sid,
                ["device"] = choice.Device.Id,
                ["factor"] = GetFactorParameterValue(choice.Factor),
                ["postAuthDestination"] = "OIDC_EXIT",
            };

            if (!passcode.IsNullOrEmpty())
                parameters["passcode"] = passcode;

            var response = PostForm<Response.SubmitFactor>("prompt", parameters, rest);
            return response.TransactionId ?? "";
        }

        // Returns null when a recoverable flow error (like incorrect code or time out) happened
        // TODO: Don't return null, use something more obvious
        internal static string SubmitFactorAndWaitForToken(string sid,
                                                           string xsrf,
                                                           DuoChoice choice,
                                                           string passcode,
                                                           IDuoUi ui,
                                                           RestClient rest)
        {
            var txid = SubmitFactor(sid, choice, passcode, rest);
            if (txid.IsNullOrEmpty())
                throw MakeInvalidResponseError("transaction ID (txid) is expected but wasn't found");

            if (!PollForResultUrl(sid, txid, ui, rest))
                return null;

            return FetchCode(sid, txid, xsrf, choice, rest);
        }

        internal static bool PollForResultUrl(string sid, string txid, IDuoUi ui, RestClient rest)
        {
            const int maxPollAttempts = 100;

            // Normally it wouldn't poll nearly as many times. Just a few at most. It either bails on error or
            // returns the result. This number here just to prevent an infinite loop, which is never a good idea.
            for (var i = 0; i < maxPollAttempts; i += 1)
            {
                var response = PostForm<R.Status>("status",
                                                  new Dictionary<string, object>
                                                  {
                                                      ["sid"] = sid,
                                                      ["txid"] = txid,
                                                  },
                                                  rest,
                                                  new Dictionary<string, string>
                                                  {
                                                      ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:109.0) Gecko/20100101 Firefox/112.0",
                                                      ["Accept"] = "*/*",
                                                      ["Accept-Language"] = "en-US,en;q=0.5",
                                                      ["Accept-Encoding"] = "gzip, deflate, br",
                                                      // TODO: Fix host
                                                      ["Referer"] = $"https://api-005dde75.duosecurity.com/frame/v4/auth/prompt?sid={sid}",
                                                      ["Sec-Fetch-Dest"] = "empty",
                                                      ["Sec-Fetch-Mode"] = "cors",
                                                      ["Sec-Fetch-Site"] = "same-origin",
                                                  });

                var (status, text) = GetResponseStatus(response);
                UpdateUi(status, text, ui);

                switch (status)
                {
                case DuoStatus.Success:
                    return true;
                case DuoStatus.Error:
                    return false;
                }

                // TODO: Need to sleep or wait here!
            }

            throw MakeInvalidResponseError("expected to receive a valid result or error, got none of it");
        }

        internal static (DuoStatus Status, string Text) GetResponseStatus(R.Status response)
        {
            var status = response.Result switch
            {
                "SUCCESS" => DuoStatus.Success,
                "FAILURE" => DuoStatus.Error,
                _ => DuoStatus.Info,
            };

            return (status, response.Reason ?? response.Code ?? "");
        }

        internal static string FetchCode(string sid, string txid, string xsrf, DuoChoice choice, RestClient rest)
        {
            var response = rest.PostForm("oidc/exit",
                                         new Dictionary<string, object>
                                         {
                                             ["sid"] = sid,
                                             ["txid"] = txid,
                                             ["factor"] = GetFactorParameterValue(choice.Factor),
                                             ["device_key"] = choice.Device.Id,
                                             ["_xsrf"] = xsrf,
                                             ["dampen_choice"] = "false",
                                         });

            if (!response.IsSuccessful)
                throw MakeSpecializedError(response);

            return ExtractCode(response.RequestUri.AbsoluteUri);
        }

        internal static string ExtractCode(string redirectUrl)
        {
            return ExtractQueryParameter(redirectUrl, "duo_code") ??
                   throw MakeInvalidResponseError($"failed to find the 'duo_code' auth token");
        }

        //
        // Data
        //

        private static readonly Dictionary<string, string> KnownSystemProperties = new Dictionary<string, string>
        {
            ["screen_resolution_width"] = "2560",
            ["screen_resolution_height"] = "1440",
            ["color_depth"] = "30",
            ["is_cef_browser"] = "false",
            ["is_ipad_os"] = "false",
            ["is_user_verifying_platform_authenticator_available"] = "false",
            ["react_support"] = "true",
        };

        private static readonly Dictionary<string, DuoFactor> StringToFactor = new Dictionary<string, DuoFactor>
        {
            ["Duo Push"] = DuoFactor.Push,
            ["Duo Mobile Passcode"] = DuoFactor.Passcode,
            ["SMS Passcode"] = DuoFactor.SendPasscodesBySms,
            ["Phone Call"] = DuoFactor.Call,
        };
    }
}
