// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

#nullable enable

using System;
using System.Collections.Generic;
using PasswordManagerAccess.Common;

namespace PasswordManagerAccess.ZohoVault
{
    public sealed class Session : IDisposable
    {
        internal Dictionary<string, string> Cookies { get; }
        internal string Domain { get; }
        internal RestClient Rest { get; }
        internal IRestTransport Transport { get; }
        internal byte[] VaultKey { get; }

        // Set lazily if/when needed
        internal byte[]? SharingKey { get; set; }

        internal Session(Dictionary<string, string> cookies, string domain, RestClient rest, IRestTransport transport, byte[] vaultKey)
        {
            Cookies = cookies;
            Domain = domain;
            Rest = rest;
            Transport = transport;
            VaultKey = vaultKey;
        }

        public void Dispose() => Transport.Dispose();
    }
}
