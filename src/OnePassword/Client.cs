// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using PasswordManagerAccess.Common;
using PasswordManagerAccess.Duo;
using PasswordManagerAccess.OnePassword.Ui;
using U2fWin10;
using R = PasswordManagerAccess.OnePassword.Response;

namespace PasswordManagerAccess.OnePassword
{
    public static class Client
    {
        public const string DefaultDomain = "my.1password.com";
        public const string ClientName = "1Password CLI";
        // TODO: Even though the CLI version doesn't seem to get outdated as quickly as the web one,
        //       it's possible this needs to be updated every now and then. Keep an eye on this.
        public const string ClientVersion = "1120401";
        public const string ClientId = ClientName + "/" + ClientVersion;

        // Public entries point to the library: Login, Logout, ListAllVaults, OpenVault
        // We try to mimic the remote structure, that's why there's an array of vaults.
        // We open all the ones we can.
        public static Session LogIn(ClientInfo clientInfo, IUi ui, ISecureStorage storage)
        {
            var transport = new RestTransport();
            try
            {
                return LogIn(clientInfo, ui, storage, transport);
            }
            catch (Exception)
            {
                transport.Dispose();
                throw;
            }
        }

        public static void LogOut(Session session)
        {
            try
            {
                LogOut(session.Rest);
            }
            finally
            {
                session.Transport.Dispose();
            }
        }

        public static VaultInfo[] ListAllVaults(Session session)
        {
            return ListAllVaults(session.ClientInfo, session.Keychain, session.Key, session.Rest);
        }

        public static Vault OpenVault(VaultInfo info, Session session)
        {
            // Make sure the vault key is in the keychain not to check on every account. They key decryption
            // is negligibly quick compared to the account retrieval, so we could do that upfront.
            info.DecryptKeyIntoKeychain();

            var accounts = GetVaultAccounts(info.Id, session.Keychain, session.Key, session.Rest);
            return new Vault(info, accounts);
        }

        // Use this function to generate a unique random identifier for each new client.
        public static string GenerateRandomUuid()
        {
            return Util.RandomUuid();
        }

        //
        // Internal
        //

        internal static Session LogIn(ClientInfo clientInfo, IUi ui, ISecureStorage storage, IRestTransport transport)
        {
            var rest = MakeRestClient(transport, GetApiUrl(clientInfo.Domain));
            var (sessionKey, sessionRest) = LogIn(clientInfo, ui, storage, rest);

            return new Session(clientInfo, new Keychain(), sessionKey, sessionRest, transport);
        }

        internal static VaultInfo[] ListAllVaults(ClientInfo clientInfo,
                                                  Keychain keychain,
                                                  AesKey sessionKey,
                                                  RestClient rest)
        {
            // Step 1: Get account info. It contains users, keys, groups, vault info and other stuff.
            //         Not the actual vault data though. That is requested separately.
            var accountInfo = GetAccountInfo(sessionKey, rest);

            // Step 2: Get all the keysets in one place. The original code is quite hairy around this
            //         topic, so it's not very clear if these keysets should be merged with anything else
            //         or it's enough to just use these keys. For now we gonna ignore other keys and
            //         see if it's enough.
            var keysets = GetKeysets(sessionKey, rest);

            // Step 3: Derive and decrypt the keys
            DecryptKeysets(keysets.Keysets, clientInfo, keychain);

            // Step 4: Get all the vaults the user has access to
            var vaults = GetAccessibleVaults(accountInfo, keychain);

            // Done
            return vaults.ToArray();
        }

        // This is exception is used internally to trigger re-login from the depth of the call stack.
        // Possibly not the best design. TODO: Should this be done differently?
        internal class RetryLoginException: Exception
        {
        }

        internal static (AesKey, RestClient) LogIn(ClientInfo clientInfo,
                                                   IUi ui,
                                                   ISecureStorage storage,
                                                   RestClient rest)
        {
            while (true)
            {
                try
                {
                    return LoginAttempt(clientInfo, ui, storage, rest);
                }
                catch (RetryLoginException)
                {
                }
            }
        }

        private static (AesKey, RestClient) LoginAttempt(ClientInfo clientInfo,
                                                         IUi ui,
                                                         ISecureStorage storage,
                                                         RestClient rest)
        {
            // Step 1: Request to initiate a new session
            var (sessionId, srpInfo) = StartNewSession(clientInfo, rest);

            // After a new session has been initiated, all the subsequent requests must be
            // signed with the session ID.
            var sessionRest = MakeRestClient(rest, sessionId: sessionId);

            // Step 2: Perform SRP exchange
            var sessionKey = Srp.Perform(clientInfo, srpInfo, sessionId, sessionRest);

            // Assign a request signer now that we have a key.
            // All the following requests are expected to be signed with the MAC.
            var macRest = MakeRestClient(sessionRest, new MacRequestSigner(sessionKey), sessionId);

            // Step 3: Verify the key with the server
            var verifiedOrMfa = VerifySessionKey(clientInfo, sessionKey, macRest);

            // Step 4: Submit 2FA code if needed
            if (verifiedOrMfa.Status == VerifyStatus.SecondFactorRequired)
                PerformSecondFactorAuthentication(verifiedOrMfa.Factors, clientInfo, sessionKey, ui, storage, macRest);

            return (sessionKey, macRest);
        }

        internal static string GetApiUrl(string domain)
        {
            return $"https://{domain}/api";
        }

        internal static RestClient MakeRestClient(IRestTransport transport,
                                                  string baseUrl,
                                                  IRequestSigner signer = null,
                                                  string sessionId = null)
        {
            var headers = new Dictionary<string, string>(2) {["X-AgileBits-Client"] = ClientId};
            if (!sessionId.IsNullOrEmpty())
                headers["X-AgileBits-Session-ID"] = sessionId;

            return new RestClient(transport, baseUrl, signer, headers);
        }

        internal static RestClient MakeRestClient(RestClient rest,
                                                  IRequestSigner signer = null,
                                                  string sessionId = null)
        {
            return MakeRestClient(rest.Transport, rest.BaseUrl, signer ?? rest.Signer, sessionId);
        }

        internal static (string SessionId, SrpInfo SrpInfo) StartNewSession(ClientInfo clientInfo, RestClient rest)
        {
            var response = rest.PostJson<R.NewSession>("v3/auth/start",
                                                       new Dictionary<string, object>
                                                       {
                                                           ["deviceUuid"] = clientInfo.Uuid,
                                                           ["email"] = clientInfo.Username,
                                                           ["skFormat"] = clientInfo.ParsedAccountKey.Format,
                                                           ["skid"] = clientInfo.ParsedAccountKey.Uuid,
                                                           ["userUuid"] = "", // TODO: Where do we get this?
                                                       });

            if (!response.IsSuccessful)
                throw MakeError(response);

            var info = response.Data;
            var status = info.Status;
            switch (status)
            {
            case "ok":
                if (info.KeyFormat != clientInfo.ParsedAccountKey.Format ||
                    info.KeyUuid != clientInfo.ParsedAccountKey.Uuid)
                    throw new BadCredentialsException("The account key is incorrect");

                var srpInfo = new SrpInfo(srpMethod: info.Auth.Method,
                                          keyMethod: info.Auth.Algorithm,
                                          iterations: info.Auth.Iterations,
                                          salt: info.Auth.Salt.Decode64Loose());

                return (info.SessionId, srpInfo);
            case "device-not-registered":
                RegisterDevice(clientInfo, MakeRestClient(rest, sessionId: info.SessionId));
                break;
            case "device-deleted":
                ReauthorizeDevice(clientInfo, MakeRestClient(rest, sessionId: info.SessionId));
                break;
            default:
                throw new InternalErrorException(
                    $"Failed to start a new session, unsupported response status '{status}'");
            }

            return StartNewSession(clientInfo, rest);
        }

        internal static void RegisterDevice(ClientInfo clientInfo, RestClient rest)
        {
            var response = rest.PostJson<R.SuccessStatus>("v1/device", new Dictionary<string, object>
            {
                ["uuid"] = clientInfo.Uuid,
                ["clientName"] = ClientName,
                ["clientVersion"] = ClientVersion,
                ["osName"] = GetOsName(),
                ["osVersion"] = "", // TODO: It's not so trivial to detect the proper OS version in .NET. Look into that.
                ["name"] = clientInfo.DeviceName,
                ["model"] = clientInfo.DeviceModel,
            });

            if (!response.IsSuccessful)
                throw MakeError(response);

            if (response.Data.Success != 1)
                throw new InternalErrorException($"Failed to register the device '{clientInfo.Uuid}'");
        }

        internal static void ReauthorizeDevice(ClientInfo clientInfo, RestClient rest)
        {
            var response = rest.Put<R.SuccessStatus>($"v1/device/{clientInfo.Uuid}/reauthorize");

            if (!response.IsSuccessful)
                throw MakeError(response);

            if (response.Data.Success != 1)
                throw new InternalErrorException($"Failed to reauthorize the device '{clientInfo.Uuid}'");
        }

        internal enum VerifyStatus
        {
            Success,
            SecondFactorRequired
        }

        internal enum SecondFactorKind
        {
            GoogleAuthenticator,
            RememberMeToken,
            WebAuthn,
            Duo,
        }

        internal readonly struct SecondFactor
        {
            public readonly SecondFactorKind Kind;
            public readonly object Parameters;

            public SecondFactor(SecondFactorKind kind, object parameters = null)
            {
                Kind = kind;
                Parameters = parameters;
            }
        }

        internal readonly struct VerifyResult
        {
            public readonly VerifyStatus Status;
            public readonly SecondFactor[] Factors;

            public VerifyResult(VerifyStatus status) : this(status, new SecondFactor[0])
            {
            }

            public VerifyResult(VerifyStatus status, SecondFactor[] factors)
            {
                Status = status;
                Factors = factors;
            }
        }

        internal static VerifyResult VerifySessionKey(ClientInfo clientInfo, AesKey sessionKey, RestClient rest)
        {
            var response = PostEncryptedJson<R.VerifyKey>(
                "v2/auth/verify",
                new Dictionary<string, object>
                {
                    ["sessionID"] = sessionKey.Id,
                    ["clientVerifyHash"] = Util.CalculateClientHash(clientInfo.ParsedAccountKey.Uuid, sessionKey.Id),
                    ["client"] = ClientId,
                    ["device"] = new Dictionary<string, string>
                    {
                        ["uuid"] = clientInfo.Uuid,
                        ["clientName"] = ClientName,
                        ["clientVersion"] = ClientVersion,
                        ["name"] = clientInfo.DeviceName,
                        ["model"] = clientInfo.DeviceModel,
                        ["osName"] = GetOsName(),
                        ["osVersion"] = "", // TODO: It's not so trivial to detect the proper OS version in .NET.
                                            // Look into that.
                        ["userAgent"] = "", // TODO: The browser uses a user agent string here. We need to figure out
                                            // what CLI sends. This is not trivial at all because of the E2E encryption.
                    },
                },
                sessionKey,
                rest);

            // TODO: 1P verifies if "serverVerifyHash" is valid. Do that.
            // We assume it's all good if we got HTTP 200.

            var mfa = response.Mfa;
            if (mfa == null)
                return new VerifyResult(VerifyStatus.Success);

            return new VerifyResult(VerifyStatus.SecondFactorRequired, GetSecondFactors(mfa));
        }

        internal static SecondFactor[] GetSecondFactors(R.MfaInfo mfa)
        {
            var factors = new List<SecondFactor>(2);

            if (mfa.GoogleAuth?.Enabled == true)
                factors.Add(new SecondFactor(SecondFactorKind.GoogleAuthenticator));

            if (mfa.RememberMe?.Enabled == true)
                factors.Add(new SecondFactor(SecondFactorKind.RememberMeToken));

            if (mfa.WebAuthn?.Enabled == true)
                factors.Add(new SecondFactor(SecondFactorKind.WebAuthn, mfa.WebAuthn));

            if (mfa.Duo?.Enabled == true)
                factors.Add(new SecondFactor(SecondFactorKind.Duo, mfa.Duo));

            if (factors.Count == 0)
                throw new InternalErrorException("No supported 2FA methods found");

            return factors.ToArray();
        }

        internal static void PerformSecondFactorAuthentication(SecondFactor[] factors,
                                                               ClientInfo clientInfo,
                                                               AesKey sessionKey,
                                                               IUi ui,
                                                               ISecureStorage storage,
                                                               RestClient rest)
        {
            // Try "remember me" first. It's possible the server didn't allow it or
            // we don't have a valid token stored from one of the previous sessions.
            if (TrySubmitRememberMeToken(factors, sessionKey, storage, rest))
                return;

            // TODO: Allow to choose 2FA method via UI like in Bitwarden
            var factor = ChooseInteractiveSecondFactor(factors);

           var secondFactorResult = GetSecondFactorResult(factor, clientInfo, ui, rest);
           if (secondFactorResult.Canceled)
                throw new CanceledMultiFactorException("Second factor step is canceled by the user");

            var token = SubmitSecondFactorResult(factor.Kind, secondFactorResult, sessionKey, rest);

            // Store the token with the application. Next time we're not gonna need to enter any passcodes.
            if (secondFactorResult.RememberMe)
                storage.StoreString(RememberMeTokenKey, token);
        }

        internal static bool TrySubmitRememberMeToken(SecondFactor[] factors,
                                                      AesKey sessionKey,
                                                      ISecureStorage storage,
                                                      RestClient rest)
        {
            if (factors.All(x => x.Kind != SecondFactorKind.RememberMeToken))
                return false;

            var token = storage.LoadString(RememberMeTokenKey);
            if (string.IsNullOrEmpty(token))
                return false;

            var result = SecondFactorResult.Done(new Dictionary<string, string>
                                                 {
                                                     ["dshmac"] = Util.HashRememberMeToken(token, sessionKey.Id)
                                                 },
                                                 true);

            try
            {
                SubmitSecondFactorResult(SecondFactorKind.RememberMeToken, result, sessionKey, rest);
            }
            catch (BadMultiFactorException)
            {
                // The token got rejected, need to erase it, it's no longer valid.
                storage.StoreString(RememberMeTokenKey, null);

                // When the stored 'remember me' token is rejected by the server, we need to try
                // the whole login sequence one more time. Probably the token is expired or it's
                // invalid.
                throw new RetryLoginException();
            }

            return true;
        }

        internal static SecondFactor ChooseInteractiveSecondFactor(SecondFactor[] factors)
        {
            if (factors.Length == 0)
                throw new InternalErrorException("The list of 2FA methods is empty");

            // We have O(N^2) here, but it's ok, since it's at most just a handful of elements.
            // Converting them to a hash set would take longer.
            foreach (var i in SecondFactorPriority)
                foreach (var f in factors)
                    if (f.Kind == i)
                        return f;

            throw new InternalErrorException("The list of 2FA methods doesn't contain any supported methods");
        }

        internal class SecondFactorResult
        {
            public readonly Dictionary<string, string> Parameters;
            public readonly bool RememberMe;
            public readonly bool Canceled;
            public readonly string Note; // Let the 2FA attach a note

            public static SecondFactorResult Done(Dictionary<string, string> parameters,
                                                  bool rememberMe,
                                                  string note = "")
            {
                return new SecondFactorResult(parameters: parameters,
                                              rememberMe: rememberMe,
                                              canceled: false,
                                              note: note);
            }

            public static SecondFactorResult Cancel()
            {
                return new SecondFactorResult(parameters: null,
                                              rememberMe: false,
                                              canceled: true);
            }

            private SecondFactorResult(Dictionary<string, string> parameters,
                                       bool rememberMe,
                                       bool canceled,
                                       string note = "")
            {
                Parameters = parameters;
                RememberMe = rememberMe;
                Canceled = canceled;
                Note = note;
            }
        }

        internal static SecondFactorResult GetSecondFactorResult(SecondFactor factor,
                                                                 ClientInfo clientInfo,
                                                                 IUi ui,
                                                                 RestClient rest)
        {
            return factor.Kind switch
            {
                SecondFactorKind.GoogleAuthenticator => AuthenticateWithGoogleAuth(ui),
                SecondFactorKind.WebAuthn => AuthenticateWithWebAuthn(factor, clientInfo, ui),
                SecondFactorKind.Duo => AuthenticateWithDuo(factor, ui, rest),
                _ => throw new InternalErrorException($"2FA method {factor.Kind} is not valid here")
            };
        }

        internal static SecondFactorResult AuthenticateWithGoogleAuth(IUi ui)
        {
            var passcode = ui.ProvideGoogleAuthPasscode();
            if (passcode == Passcode.Cancel)
                return SecondFactorResult.Cancel();

            return SecondFactorResult.Done(new Dictionary<string, string>
                                           {
                                               ["code"] = passcode.Code,
                                           },
                                           passcode.RememberMe);
        }

        internal static SecondFactorResult AuthenticateWithWebAuthn(SecondFactor factor, ClientInfo clientInfo, IUi ui)
        {
            var rememberMe = ui.ProvideWebAuthnRememberMe();
            if (rememberMe == Passcode.Cancel)
                return SecondFactorResult.Cancel();

            if (!(factor.Parameters is R.WebAuthnMfa extra))
                throw new InternalErrorException("WebAuthn extra parameters expected");

            if (extra.KeyHandles.Length > 1)
                throw new UnsupportedFeatureException("Multiple WebAuthn keys are not supported");

            try
            {
                var assertion = WebAuthN.GetAssertion(appId: "1password.com",
                                                      challenge: extra.Challenge,
                                                      origin: $"https://{clientInfo.Domain}",
                                                      crossOrigin: false,
                                                      keyHandle: extra.KeyHandles[0]);

                return SecondFactorResult.Done(new Dictionary<string, string>
                                               {
                                                   ["keyHandle"] = assertion.KeyHandle,
                                                   ["signature"] = assertion.Signature,
                                                   ["authData"] = assertion.AuthData,
                                                   ["clientData"] = assertion.ClientData,
                                               },
                                               rememberMe.RememberMe);
            }
            catch (CanceledException)
            {
                return SecondFactorResult.Cancel();
            }
            catch (ErrorException e)
            {
                throw new InternalErrorException("WebAuthn authentication failed", e);
            }
        }

        internal static SecondFactorResult AuthenticateWithDuo(SecondFactor factor, IUi ui, RestClient rest)
        {
            if (!(factor.Parameters is R.DuoMfa extra))
                throw new InternalErrorException("Duo extra parameters expected");

            static string CheckParam(string param, string name)
            {
                if (param.IsNullOrEmpty())
                    throw new InternalErrorException($"Duo parameter '{name}' is invalid");

                return param;
            }

            var isV1 = extra.Url.IsNullOrEmpty();
            var result = isV1
                ? DuoV1.Authenticate(CheckParam(extra.Host, "host"),
                                     CheckParam(extra.Signature, "sigRequest"),
                                     ui,
                                     rest.Transport)
                : DuoV4.Authenticate(extra.Url, ui, rest.Transport);

            if (result == null)
                return SecondFactorResult.Cancel();

            var key = isV1 ? "sigResponse" : "code";
            var note = isV1 ? "v1" : "v4";
            return SecondFactorResult.Done(new Dictionary<string, string> { [key] = result.Passcode },
                                           result.RememberMe,
                                           note);
        }

        // Returns "remember me" token when successful
        internal static string SubmitSecondFactorResult(SecondFactorKind factor,
                                                        SecondFactorResult result,
                                                        AesKey sessionKey,
                                                        RestClient rest)
        {
            var key = factor switch
            {
                SecondFactorKind.GoogleAuthenticator => "totp",
                SecondFactorKind.RememberMeToken => "dsecret",
                SecondFactorKind.WebAuthn => "webAuthn",
                SecondFactorKind.Duo => result.Note switch
                {
                    "v1" => "duo",
                    "v4" => "duov4",
                    _ => throw new InternalErrorException($"Invalid Duo version '{result.Note}'"),
                },
                _ => throw new InternalErrorException($"2FA method {factor} is not valid"),
            };

            try
            {
                var response = PostEncryptedJson<R.Mfa>("v1/auth/mfa",
                                                        new Dictionary<string, object>
                                                        {
                                                            ["sessionID"] = sessionKey.Id,
                                                            ["client"] = ClientId,
                                                            [key] = result.Parameters,
                                                        },
                                                        sessionKey,
                                                        rest);

                return response.RememberMeToken;
            }
            catch (BadCredentialsException e)
            {
                // The server report everything as "no auth" error. In this case we know it's related to the MFA.
                throw new BadMultiFactorException("Incorrect second factor code", e.InnerException);
            }
        }

        internal static R.AccountInfo GetAccountInfo(AesKey sessionKey, RestClient rest)
        {
            return GetEncryptedJson<R.AccountInfo>(
                "v1/account?attrs=billing,counts,groups,invite,me,settings,tier,user-flags,users,vaults",
                sessionKey,
                rest);
        }

        internal static R.KeysetsInfo GetKeysets(AesKey sessionKey, RestClient rest)
        {
            return GetEncryptedJson<R.KeysetsInfo>("v1/account/keysets", sessionKey, rest);
        }

        internal static IEnumerable<VaultInfo> GetAccessibleVaults(R.AccountInfo accountInfo, Keychain keychain)
        {
            return from vault in accountInfo.Vaults
                let key = FindWorkingKey(vault.Access, keychain)
                where key != null
                select new VaultInfo(vault.Id, Encrypted.Parse(vault.Attributes), key, keychain);
        }

        internal static Encrypted FindWorkingKey(R.VaultAccessInfo[] accessList, Keychain keychain)
        {
            foreach (var access in accessList)
            {
                if (IsReadAccessible(access.Acl))
                {
                    var key = Encrypted.Parse(access.EncryptedKey);
                    if (keychain.CanDecrypt(key))
                        return key;
                }
            }

            return null;
        }

        internal static bool IsReadAccessible(int acl)
        {
            const int haveReadAccess = 32;
            return (acl & haveReadAccess) != 0;
        }

        internal static Account[] GetVaultAccounts(string id,
                                                   Keychain keychain,
                                                   AesKey sessionKey,
                                                   RestClient rest)
        {
            return EnumerateAccountsItemsInVault(id, sessionKey, rest)
                .Where(ShouldKeepAccount)
                .Select(itemInfo => new Account(itemInfo, keychain))
                .ToArray();
        }

        // TODO: Rename to RequestVaultAccounts? It should clearer from the name that it's a slow operation.
        // Don't enumerate more than once. It's very slow since it makes network requests.
        internal static IEnumerable<R.VaultItem> EnumerateAccountsItemsInVault(string id,
                                                                               AesKey sessionKey,
                                                                               RestClient rest)
        {
            var batchId = 0;
            while (true)
            {
                var batch = GetEncryptedJson<R.VaultItemsBatch>($"v1/vault/{id}/{batchId}/items", sessionKey, rest);
                if (batch.Items != null)
                    foreach (var i in batch.Items)
                        yield return i;

                // The last batch is marked with {batchComplete: true}
                if (batch.Complete)
                    yield break;

                batchId = batch.Version;
            }
        }

        // TODO: Add a test to verify the deleted accounts are ignored
        internal static bool ShouldKeepAccount(R.VaultItem account)
        {
            // Reject everything but accounts/logins
            if (!Account.SupportedTemplateIds.Contains(account.TemplateId))
                return false;

            // Reject deleted accounts (be conservative, throw only explicitly marked as "Y")
            if (account.Deleted == "Y")
                return false;

            return true;
        }

        internal static void LogOut(RestClient rest)
        {
            var response = rest.Put<R.SuccessStatus>("v1/session/signout");

            if (!response.IsSuccessful)
                throw MakeError(response);

            if (response.Data.Success != 1)
                throw new InternalErrorException("Failed to logout");
        }

        internal static void DecryptKeysets(R.KeysetInfo[] keysets, ClientInfo clientInfo, Keychain keychain)
        {
            // Find the master keyset
            var masterKeyset = keysets
                .Where(x => x.EncryptedBy == MasterKeyId)
                .OrderByDescending(x => x.SerialNumber)
                .FirstOrDefault();

            if (masterKeyset is null)
                throw new InternalErrorException("Master keyset not found");

            // Derive the master key. The rest of the keyset should decrypt by master or its derivatives.
            var keyInfo = masterKeyset.KeyOrMasterKey;
            var masterKey = DeriveMasterKey(algorithm: keyInfo.Algorithm,
                                            iterations: keyInfo.Iterations,
                                            salt: keyInfo.Salt.Decode64Loose(),
                                            clientInfo: clientInfo);
            keychain.Add(masterKey);

            // Build a topological map: key -> other keys encrypted by that key
            var encryptsKeysets = new Dictionary<string, List<R.KeysetInfo>>();
            foreach (var keyset in keysets)
                encryptsKeysets.GetOrAdd(GetEncryptedBy(keyset), () => new List<R.KeysetInfo>()).Add(keyset);

            // Start from "mp" and topologically walk the keys in a valid decryption order
            var queue = new Queue<R.KeysetInfo>(encryptsKeysets[MasterKeyId]);
            while (queue.Count > 0)
            {
                var keyset = queue.Dequeue();
                DecryptKeyset(keyset, keychain);

                // If the newly decrypted key encrypts some other keys, add them to the back of the queue
                if (encryptsKeysets.TryGetValue(keyset.Id, out var newKeysets))
                    foreach (var newKeyset in newKeysets)
                        queue.Enqueue(newKeyset);
            }
        }

        internal static string GetEncryptedBy(R.KeysetInfo keyset)
        {
            return keyset.EncryptedBy.IsNullOrEmpty()
                ? keyset.KeyOrMasterKey.KeyId
                : keyset.EncryptedBy;
        }

        internal static void DecryptKeyset(R.KeysetInfo keyset, Keychain keychain)
        {
            Util.DecryptAesKey(keyset.KeyOrMasterKey, keychain);
            Util.DecryptRsaKey(keyset.PrivateKey, keychain);
        }

        internal static AesKey DeriveMasterKey(string algorithm,
                                               int iterations,
                                               byte[] salt,
                                               ClientInfo clientInfo)
        {
            // TODO: Check if the Unicode normalization is the correct one. This could be done
            //       by either trying to call the original JS functions in the browser console
            //       or by changing to some really weird password and trying to log in.

            var k1 = Util.Hkdf(algorithm, salt, clientInfo.Username.ToLowerInvariant().ToBytes());
            var k2 = Util.Pbes2(algorithm, clientInfo.Password.Normalize(), k1, iterations);
            var key = clientInfo.ParsedAccountKey.CombineWith(k2);

            return new AesKey(MasterKeyId, key);
        }

        //
        // HTTP
        //

        internal static BaseException MakeError(RestResponse<string> response)
        {
            if (response.IsNetworkError)
                return new NetworkErrorException("Network error has occurred", response.Error);

            var serverError = ParseServerError(response.Content);
            if (serverError != null)
                return serverError;

            return new InternalErrorException(
                $"Invalid or unexpected response from the server (HTTP status: {response.StatusCode})",
                response.Error);
        }

        // Returns null when no error is found
        internal static BaseException ParseServerError(string response)
        {
            try
            {
                var error = JsonConvert.DeserializeObject<R.Error>(response);
                switch (error.Code)
                {
                case 102:
                    return new BadCredentialsException("Username, password or account key is incorrect");
                default:
                    return new InternalErrorException(
                        $"The server responded with the error code {error.Code} and the message '{error.Message}'");
                }
            }
            catch (JsonException)
            {
                // Ignore, it wasn't a server error
            }

            try
            {
                var reason = JsonConvert.DeserializeObject<R.FailureReason>(response).Reason;
                if (!reason.IsNullOrEmpty())
                    return new InternalErrorException($"The server responded with the failure reason: '{reason}'");
            }
            catch (JsonException)
            {
                // Ignore, it wasn't a server error
            }

            return null;
        }

        internal static T GetEncryptedJson<T>(string endpoint,
                                              AesKey sessionKey,
                                              RestClient rest)
        {
            var response = rest.Get<R.Encrypted>(endpoint);
            if (!response.IsSuccessful)
                throw MakeError(response);

            return DecryptResponse<T>(response.Data, sessionKey);
        }

        internal static T PostEncryptedJson<T>(string endpoint,
                                               Dictionary<string, object> parameters,
                                               AesKey sessionKey,
                                               RestClient rest)
        {
            var payload = JsonConvert.SerializeObject(parameters);
            var encryptedPayload = sessionKey.Encrypt(payload.ToBytes());

            var response = rest.PostJson<R.Encrypted>(endpoint, encryptedPayload.ToDictionary());
            if (!response.IsSuccessful)
                throw MakeError(response);

            return DecryptResponse<T>(response.Data, sessionKey);
        }

        internal static T DecryptResponse<T>(R.Encrypted encrypted, IDecryptor decryptor)
        {
            var plaintext = decryptor.Decrypt(Encrypted.Parse(encrypted)).ToUtf8();

            // First check for server errors. It's possible to deserialize the returned error object
            // into one of the target types by mistake when the type has no mandatory fields.
            // `Response.Mfa` would be one of those.
            if (ParseServerError(plaintext) is {} serverError)
                throw serverError;

            try
            {
                return JsonConvert.DeserializeObject<T>(plaintext);
            }
            catch (JsonException e)
            {
                throw new InternalErrorException("Failed to parse JSON in response from the server", e);
            }
        }

        internal static string GetOsName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "Windows";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "macOS";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "Linux";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("iOS")))
                return "iOS";

            return "Unknown";
        }

        //
        // Private
        //

        private const string MasterKeyId = "mp";
        private const string RememberMeTokenKey = "remember-me-token";

        private static SecondFactorKind[] SecondFactorPriority =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? SecondFactorPriorityWindows
                : SecondFactorPriorityOtherPlatforms;

        private static readonly SecondFactorKind[] SecondFactorPriorityWindows =
        {
            SecondFactorKind.WebAuthn,
            SecondFactorKind.Duo,
            SecondFactorKind.GoogleAuthenticator,
        };

        private static readonly SecondFactorKind[] SecondFactorPriorityOtherPlatforms =
        {
            SecondFactorKind.Duo,
            SecondFactorKind.GoogleAuthenticator,
        };
    }
}
