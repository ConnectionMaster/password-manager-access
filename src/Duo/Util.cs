// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Collections.Generic;
using HtmlAgilityPack;
using PasswordManagerAccess.Common;
using PasswordManagerAccess.Duo.Response;

namespace PasswordManagerAccess.Duo
{
    // Only for internal use of Duo.*
    internal static class Util
    {
        internal static HtmlDocument Parse(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc;
        }

        internal static T PostForm<T>(string endpoint,
                                      Dictionary<string, object> parameters,
                                      RestClient rest,
                                      Dictionary<string, string> extraHeaders = null)
        {
            var response = rest.PostForm<Envelope<T>>(endpoint, parameters, headers: extraHeaders);

            // All good
            if (response.IsSuccessful && response.Data.Status == "OK" && response.Data.Payload != null)
                return response.Data.Payload;

            throw MakeSpecializedError(response);
        }

        // Returns null when not found
        internal static string ExtractQueryParameter(string url, string name)
        {
            var nameEquals = name + '=';
            var start = url.IndexOf(nameEquals, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += nameEquals.Length;
            var end = url.IndexOf('&', start);

            return end < 0
                ? url.Substring(start) // The last parameter
                : url.Substring(start, end - start);
        }

        internal static string GetFactorParameterValue(DuoFactor factor)
        {
            return factor switch
            {
                DuoFactor.Push => "Duo Push",
                DuoFactor.Call => "Phone Call",
                DuoFactor.Passcode => "Passcode",
                DuoFactor.SendPasscodesBySms => "sms",
                _ => ""
            };
        }

        internal static void UpdateUi(DuoStatus status, string text, IDuoUi ui)
        {
            if (text.IsNullOrEmpty())
                return;

            ui.UpdateDuoStatus(status, text);
        }

        internal static InternalErrorException MakeInvalidResponseError(string message)
        {
            return new InternalErrorException(ErrorPrefix + message);
        }

        internal static BaseException MakeSpecializedError(RestResponse response, string extraInfo = "")
        {
            var text = ErrorPrefix + $"rest call to {response.RequestUri} failed";

            if (response.IsHttpError)
                text += $" (HTTP status: {response.StatusCode})";

            if (!extraInfo.IsNullOrEmpty())
                text += extraInfo;

            return new InternalErrorException(text, response.Error);
        }

        internal static BaseException MakeSpecializedError<T>(RestResponse<string, Envelope<T>> response)
        {
            var message = response.Data.Message.IsNullOrEmpty() ? "none" : response.Data.Message;
            return MakeSpecializedError(response, $"Server message: {message}");
        }

        //
        // Data
        //

        private const string ErrorPrefix = "Duo: ";
    }
}
