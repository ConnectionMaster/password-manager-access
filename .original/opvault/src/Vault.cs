// Copyright (C) 2018 Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OPVault
{
    public class Vault
    {
        internal static JObject LoadJsAsJson(string filename, string prefix, string suffix)
        {
            return LoadJsAsJsonFromString(File.ReadAllText(filename), prefix, suffix);
        }

        internal static JObject LoadJsAsJsonFromString(string content, string prefix, string suffix)
        {
            // TODO: Use custom exception
            if (content.Length < prefix.Length + suffix.Length)
                throw new InvalidOperationException("Content is too short");
            if (!content.StartsWith(prefix))
                throw new InvalidOperationException("Expected prefix is not found in content");
            if (!content.EndsWith(suffix))
                throw new InvalidOperationException("Expected suffix is not found in content");

            return JObject.Parse(content.Substring(prefix.Length, content.Length - prefix.Length - suffix.Length));
        }

        internal static string MakeFilename(string path, string filename)
        {
            return Path.Combine(NormalizeSlashes(path), "default", NormalizeSlashes(filename));
        }

        internal static string NormalizeSlashes(string path)
        {
            // TODO: Test on non Windows based platforms
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
    }
}
