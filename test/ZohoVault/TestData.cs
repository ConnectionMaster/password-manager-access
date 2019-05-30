// Copyright (C) 2012-2019 Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using PasswordManagerAccess.Common;

namespace PasswordManagerAccess.Test.ZohoVault
{
    static class TestData
    {
        // Calculated with the original Js code
        public static readonly byte[] Key = "d7643007973dba7243d724f66fd806bf".ToBytes();
        public const string Passphrase = "passphrase123";
    }
}
