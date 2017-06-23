// Copyright (C) 2017 Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

namespace OnePassword
{
    internal class AesKey
    {
        public const string ContainerType = "b5+jwk+json";
        public const string EncryptionScheme = "A256GCM";

        public readonly string Id;
        public readonly byte[] Key;

        public AesKey(string id, byte[] key)
        {
            Id = id;
            Key = key;
        }

        public byte[] Encrypt(byte[] plaintext, byte[] iv)
        {
            // TODO: Implement this
            return plaintext;
        }

        public byte[] Decrypt(byte[] ciphertext, byte[] iv)
        {
            // TODO: Implement this
            return ciphertext;
        }
    }
}
