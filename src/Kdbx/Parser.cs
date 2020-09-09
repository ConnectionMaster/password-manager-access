// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PasswordManagerAccess.Common;

namespace PasswordManagerAccess.Kdbx
{
    internal static class Parser
    {
        public static void Parse(string filename, string password)
        {
            Parse(File.ReadAllBytes(filename), password);
        }

        //
        // Internal
        //

        internal static void Parse(byte[] blob, string password)
        {
            var io = blob.AsRoSpan().ToStream();
            var header = io.Read<Header>();

            if (header.Signature1 != Magic1)
                throw MakeInvalidFormatError($"primary file signature is invalid: {header.Signature1:x8}");

            if (!Magic2.Contains(header.Signature2))
                throw MakeInvalidFormatError($"secondary file signature is invalid: {header.Signature2:x8}");

            if (header.MajorVersion != Version4)
                throw MakeUnsupportedError($"Version {header.MajorVersion}.{header.MinorVersion}");

            ReadFields(io);
        }

        internal static void ReadFields(SpanStream io)
        {
            for (;;)
            {
                var header = io.Read<FieldHeader>();
                var payload = io.ReadBytes(header.Size);

                switch (header.Id)
                {
                // Done (payload ignored)
                case 0:
                    return;

                // Cipher
                case 2:
                    if (payload.Length != 16)
                        throw MakeInvalidFormatError($"cipher field has incorrect size ({payload.Length})");

                    if (payload.SequenceEqual(AesCipherId))
                        Console.WriteLine("Cipher: AES");
                    else if (payload.SequenceEqual(ChaCha20CipherId))
                        Console.WriteLine("Cipher: ChaCha20");
                    else if (payload.SequenceEqual(TwoFishCipherId))
                        Console.WriteLine("Cipher: TwoFish");
                    else
                        throw MakeUnsupportedError($"Cipher '{payload.ToHex()}'");

                    break;

                // Compression method
                case 3:
                    if (payload.Length != 4)
                        throw MakeInvalidFormatError($"compression method field has incorrect size ({payload.Length})");

                    var compression = new SpanStream(payload).ReadUInt32();
                    switch (compression)
                    {
                    case 0:
                        Console.WriteLine("Not compressed");
                        break;
                    case 1:
                        Console.WriteLine("GZip compressed");
                        break;
                    default:
                        throw MakeUnsupportedError($"Compression method {compression}");
                    }
                    break;

                // Master seed
                case 4:
                    Console.WriteLine($"Master seed: {payload.ToHex()}");
                    break;

                // Master IV
                case 7:
                    Console.WriteLine($"Master IV: {payload.ToHex()}");
                    break;

                // KDF parameters
                case 11:
                    var kdf = ReadVariantDictionary(payload);
                    foreach (var i in kdf)
                    {
                        var value = i.Value.ToString();
                        if (i.Value is byte[] bytes)
                            value = bytes.ToHex();

                        Console.WriteLine($"  - {i.Key}: {value}");
                    }

                    break;

                // Other fields are ignored
                }
            }
        }

        private static Dictionary<string, object> ReadVariantDictionary(ReadOnlySpan<byte> span)
        {
            return ReadVariantDictionary(span.ToStream());
        }

        private static Dictionary<string, object> ReadVariantDictionary(SpanStream io)
        {
            var version = io.ReadUInt16();
            if (version != 0x0100)
                throw MakeUnsupportedError($"Variant dictionary version {version}");

            var result = new Dictionary<string, object>();

            for (;;)
            {
                var type = io.ReadByte();
                if (type == 0)
                    return result;

                var keySize = io.ReadInt32();
                var key = io.ReadBytes(keySize).ToUtf8();
                var valueSize = io.ReadInt32();

                result[key] = type switch
                {
                    // UInt32
                    4 => io.ReadUInt32(),

                    // UInt64
                    5 => io.ReadUInt64(),

                    // Bool
                    8 => io.ReadByte() != 0,

                    // Int32
                    12 => io.ReadInt32(),

                    // Int64
                    13 => io.ReadInt64(),

                    // UTF-8 string
                    24 => io.ReadBytes(valueSize).ToUtf8(),

                    // byte[]
                    66 => io.ReadBytes(valueSize).ToArray(),

                    _ => throw MakeInvalidFormatError($"item type {type} is invalid"),
                };
            }
        }

        internal static BaseException MakeInvalidFormatError(string message)
        {
            return new InternalErrorException($"Invalid format: {message}");
        }

        internal static BaseException MakeUnsupportedError(string message)
        {
            return new UnsupportedFeatureException($"{message} is not supported");
        }

        //
        // Models
        //

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal readonly struct Header
        {
            public readonly uint Signature1;
            public readonly uint Signature2;
            public readonly ushort MinorVersion;
            public readonly ushort MajorVersion;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal readonly struct FieldHeader
        {
            public readonly byte Id;
            public readonly int Size;
        }

        //
        // Data
        //

        internal const uint Magic1 = 0x9aa2d903u;
        internal static readonly uint[] Magic2 = {0xb54bfb66u, 0xb54bfb67u};
        internal const ushort Version4 = 4;

        internal static readonly byte[] AesCipherId =
        {
            0x31, 0xc1, 0xf2, 0xe6, 0xbf, 0x71, 0x43, 0x50,
            0xbe, 0x58, 0x05, 0x21, 0x6a, 0xfc, 0x5a, 0xff,
        };

        internal static readonly byte[] ChaCha20CipherId =
        {
            0xD6, 0x03, 0x8a, 0x2b, 0x8b, 0x6f, 0x4c, 0xB5,
            0xa5, 0x24, 0x33, 0x9a, 0x31, 0xdb, 0xb5, 0x9a,
        };

        internal static readonly byte[] TwoFishCipherId =
        {
            0xad, 0x68, 0xf2, 0x9f, 0x57, 0x6f, 0x4b, 0xb9,
            0xa3, 0x6a, 0xd4, 0x7a, 0xf9, 0x65, 0x34, 0x6c,
        };
    }
}
