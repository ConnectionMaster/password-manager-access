// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using PasswordManagerAccess.Common;
using Shouldly;
using Xunit;

namespace PasswordManagerAccess.Test.Common
{
    // TODO: Add global usings to get rid of these!
    using HttpCookies = Dictionary<string, string>;
    using HttpHeaders = Dictionary<string, string>;
    using PostParameters = Dictionary<string, object>;
    using SendAsyncType = Func<HttpRequestMessage, Task<HttpResponseMessage>>;

    // TODO: Test redirects
    public class RestClientTest
    {
        [Fact]
        public void IsSuccessful_is_false_on_null_data()
        {
            var response = Serve("").Get<object>(Url);

            Assert.Null(response.Data);
            Assert.False(response.IsSuccessful);
        }

        //
        // Header operations
        //

        [Fact]
        public void Header_operations_sequence_constructor_add_update_remove()
        {
            // Arrange
            var initialHeaders = new HttpHeaders
            {
                ["X-Initial"] = "initial-value",
                ["X-ToUpdate"] = "original-value",
                ["X-ToRemove"] = "remove-me",
            };
            var rest = new RestClient(null, defaultHeaders: initialHeaders);

            // Act
            rest.AddOrUpdateHeader("X-New", "new-value"); // Add new
            rest.AddOrUpdateHeader("X-ToUpdate", "updated-value"); // Update existing
            rest.RemoveHeader("X-ToRemove"); // Remove existing

            // Assert
            rest.DefaultHeaders.ShouldBeEquivalentTo(
                new HttpHeaders
                {
                    ["X-Initial"] = "initial-value",
                    ["X-ToUpdate"] = "updated-value",
                    ["X-New"] = "new-value",
                }
            );
        }

        [Fact]
        public void Constructor_creates_independent_copy_of_headers()
        {
            // Arrange
            var initial = new HttpHeaders { ["X-Test"] = "original" };
            var rest = new RestClient(null, defaultHeaders: initial);

            // Act
            initial["X-Test"] = "modified";
            initial["X-New"] = "new-value";

            // Assert
            rest.DefaultHeaders.ShouldBeEquivalentTo(new HttpHeaders { ["X-Test"] = "original" });
        }

        [Fact]
        public void Constructor_creates_independent_copy_of_empty_headers()
        {
            // Arrange
            var initial = new HttpHeaders();
            var rest = new RestClient(null, defaultHeaders: initial);

            // Act
            initial["X-Test"] = "modified";
            initial["X-New"] = "new-value";

            // Assert
            rest.DefaultHeaders.ShouldBeEquivalentTo(new HttpHeaders());
        }

        //
        // Cookie operations
        //

        [Fact]
        public void AddOrUpdateCookie_adds_and_updates_cookies()
        {
            // Arrange
            var rest = new RestClient(
                null,
                defaultCookies: new HttpCookies
                {
                    ["X-Initial"] = "initial-value",
                    ["X-ToUpdate"] = "original-value",
                    ["X-ToRemove"] = "remove-me",
                }
            );

            // Act
            rest.AddOrUpdateCookie("X-New", "new-value"); // Add new
            rest.AddOrUpdateCookie("X-ToUpdate", "updated-value"); // Update existing
            rest.RemoveCookie("X-ToRemove"); // Remove existing

            // Assert
            rest.DefaultCookies.ShouldBeEquivalentTo(
                new HttpCookies
                {
                    ["X-Initial"] = "initial-value",
                    ["X-ToUpdate"] = "updated-value",
                    ["X-New"] = "new-value",
                }
            );
        }

        [Fact]
        public void Constructor_creates_independent_copy_of_cookies()
        {
            // Arrange
            var initial = new HttpCookies { ["X-Test"] = "original" };
            var rest = new RestClient(null, defaultCookies: initial);

            // Act
            initial["X-Test"] = "modified";
            initial["X-New"] = "new-value";

            // Assert
            rest.DefaultCookies.ShouldBeEquivalentTo(new HttpCookies { ["X-Test"] = "original" });
        }

        [Fact]
        public void Constructor_creates_independent_copy_of_empty_cookies()
        {
            // Arrange
            var initial = new HttpCookies();
            var rest = new RestClient(null, defaultCookies: initial);

            // Act
            initial["X-Test"] = "modified";
            initial["X-New"] = "new-value";

            // Assert
            rest.DefaultCookies.ShouldBeEquivalentTo(new HttpCookies());
        }

        //
        // GET
        //

        [Fact]
        public void Get_works()
        {
            var response = Serve("yo").Get(Url);

            Assert.True(response.IsSuccessful);
            Assert.Equal("yo", response.Content);
        }

        [Fact]
        public void Get_sets_url()
        {
            InRequest(rest => rest.Get(Url), request => Assert.Equal(Url, request.RequestUri.AbsoluteUri));
        }

        [Fact]
        public void Get_sends_headers()
        {
            InRequest(
                rest => rest.Get(Url, headers: TestHeaders),
                request => Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Get_sends_cookies()
        {
            InRequest(rest => rest.Get(Url, cookies: TestCookies), request => Assert.Equal([TestCookieHeader], request.Headers.GetValues("Cookie")));
        }

        [Fact]
        public void Get_decodes_json()
        {
            var response = Serve("""{"Key":"k","Value":"v"}""").Get<KeyValuePair<string, string>>(Url);

            Assert.True(response.IsSuccessful);
            Assert.Equal(new KeyValuePair<string, string>("k", "v"), response.Data);
        }

        [Fact]
        public void Get_returns_binary_content()
        {
            var content = "binary".ToBytes();
            var response = Serve(content).GetBinary(Url);

            Assert.Equal(content, response.Content);
        }

        [Fact]
        public void Get_returns_response_headers()
        {
            var response = Serve("", ResponseHeaders).Get(Url);

            Assert.Equal(ResponseHeaders, response.Headers);
        }

        //
        // POST JSON/form
        //

        [Fact]
        public void PostJson_sends_json_headers()
        {
            InRequest(
                rest => rest.PostJson(Url, RestClient.NoParameters),
                request => Assert.Equal(["application/json; charset=utf-8"], request.Content.Headers.GetValues("Content-type"))
            );
        }

        [Fact]
        public void PostJson_encodes_json()
        {
            InRequest(
                rest => rest.PostJson(Url, new PostParameters { ["k"] = "v" }),
                request => Assert.Equal("""{"k":"v"}""", request.Content.ReadAsStringAsync().Result)
            );
        }

        [Fact]
        public void PostJson_with_NoParameters_sends_empty_object()
        {
            InRequest(rest => rest.PostJson(Url, RestClient.NoParameters), request => Assert.Equal("{}", request.Content.ReadAsStringAsync().Result));
        }

        [Fact]
        public void PostJson_with_JsonBlank_sends_blank()
        {
            InRequest(rest => rest.PostJson(Url, RestClient.JsonBlank), request => Assert.Equal("", request.Content.ReadAsStringAsync().Result));
        }

        [Fact]
        public void PostJson_with_JsonNull_sends_null()
        {
            InRequest(rest => rest.PostJson(Url, RestClient.JsonNull), request => Assert.Equal("null", request.Content.ReadAsStringAsync().Result));
        }

        [Fact]
        public void PostForm_sends_form_headers()
        {
            InRequest(
                rest => rest.PostForm(Url, RestClient.NoParameters),
                request => Assert.Equal(["application/x-www-form-urlencoded"], request.Content.Headers.GetValues("Content-type"))
            );
        }

        [Fact]
        public void PostJson_encodes_form()
        {
            InRequest(
                rest => rest.PostForm(Url, new PostParameters { ["k"] = "v" }),
                request => Assert.Equal("k=v", request.Content.ReadAsStringAsync().Result)
            );
        }

        [Fact]
        public void PostJson_returns_response_headers()
        {
            var response = Serve("", ResponseHeaders).PostJson(Url, RestClient.NoParameters);

            Assert.Equal(ResponseHeaders, response.Headers);
        }

        [Fact]
        public void PostJson_decodes_json()
        {
            var response = Serve("""{"Key":"k","Value":"v"}""").PostJson<KeyValuePair<string, string>>(Url, RestClient.NoParameters);

            Assert.True(response.IsSuccessful);
            Assert.Equal(new KeyValuePair<string, string>("k", "v"), response.Data);
        }

        [Fact]
        public void PostRaw_sends_content_as_is()
        {
            var content = "blah-blah...";
            InRequest(rest => rest.PostRaw(Url, content), request => Assert.Equal(content, request.Content.ReadAsStringAsync().Result));
        }

        //
        // PUT
        //

        [Fact]
        public void Put_works()
        {
            var response = Serve("yo").Put(Url);

            Assert.True(response.IsSuccessful);
            Assert.Equal("yo", response.Content);
        }

        [Fact]
        public void Put_sets_url()
        {
            InRequest(rest => rest.Put(Url), request => Assert.Equal(Url, request.RequestUri.AbsoluteUri));
        }

        [Fact]
        public void Put_sends_headers()
        {
            InRequest(
                rest => rest.Put(Url, headers: TestHeaders),
                request => Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Put_sends_cookies()
        {
            InRequest(
                rest => rest.Put(Url, cookies: TestCookies),
                request => Assert.Equal(new[] { TestCookieHeader }, request.Headers.GetValues("Cookie"))
            );
        }

        [Fact]
        public void Put_decodes_json()
        {
            var response = Serve("""{"Key":"k","Value":"v"}""").Put<KeyValuePair<string, string>>(Url);

            Assert.True(response.IsSuccessful);
            Assert.Equal(new KeyValuePair<string, string>("k", "v"), response.Data);
        }

        [Fact]
        public void Put_returns_response_headers()
        {
            var response = Serve("", ResponseHeaders).Put(Url);

            Assert.Equal(ResponseHeaders, response.Headers);
        }

        //
        // PUT JSON
        //

        [Fact]
        public void PutJson_sends_json_headers()
        {
            InRequest(
                rest => rest.PutJson(Url, RestClient.NoParameters),
                request => Assert.Equal(["application/json; charset=utf-8"], request.Content.Headers.GetValues("Content-type"))
            );
        }

        [Fact]
        public void PutJson_encodes_json()
        {
            InRequest(
                rest => rest.PutJson(Url, new PostParameters { ["k"] = "v" }),
                request => Assert.Equal("""{"k":"v"}""", request.Content.ReadAsStringAsync().Result)
            );
        }

        [Fact]
        public void PutJson_with_NoParameters_sends_empty_object()
        {
            InRequest(rest => rest.PutJson(Url, RestClient.NoParameters), request => Assert.Equal("{}", request.Content.ReadAsStringAsync().Result));
        }

        [Fact]
        public void PutJson_with_JsonBlank_sends_blank()
        {
            InRequest(rest => rest.PutJson(Url, RestClient.JsonBlank), request => Assert.Equal("", request.Content.ReadAsStringAsync().Result));
        }

        [Fact]
        public void PutJson_with_JsonNull_sends_null()
        {
            InRequest(rest => rest.PutJson(Url, RestClient.JsonNull), request => Assert.Equal("null", request.Content.ReadAsStringAsync().Result));
        }

        [Fact]
        public void PutJson_returns_response_headers()
        {
            var response = Serve("", ResponseHeaders).PutJson(Url, RestClient.NoParameters);

            Assert.Equal(ResponseHeaders, response.Headers);
        }

        [Fact]
        public void PutJson_decodes_json()
        {
            var response = Serve("""{"Key":"k","Value":"v"}""").PutJson<KeyValuePair<string, string>>(Url, RestClient.NoParameters);

            Assert.True(response.IsSuccessful);
            Assert.Equal(new KeyValuePair<string, string>("k", "v"), response.Data);
        }

        //
        // URI
        //

        [Theory]
        // Only domain
        [InlineData("http://all.your.base", "are/belong/to/us")]
        [InlineData("http://all.your.base", "/are/belong/to/us")]
        [InlineData("http://all.your.base/", "are/belong/to/us")]
        [InlineData("http://all.your.base/", "/are/belong/to/us")]
        // Domain with path
        [InlineData("http://all.your.base/are", "belong/to/us")]
        [InlineData("http://all.your.base/are", "/belong/to/us")]
        [InlineData("http://all.your.base/are/", "belong/to/us")]
        [InlineData("http://all.your.base/are/", "/belong/to/us")]
        public void MakeAbsoluteUri_joins_url_with_slashes(string baseUrl, string endpoint)
        {
            var rest = new RestClient(null, baseUrl);
            Assert.Equal("http://all.your.base/are/belong/to/us", rest.MakeAbsoluteUri(endpoint).AbsoluteUri);
        }

        [Theory]
        // There's one special case here: when joining 'http://domain.tld' and '?endpoint'
        // there should be no slash inserted, but the Uri constructor inserts one anyway.
        // So we account for this special behavior in the tests.
        [InlineData("http://domiain.tld", "?endpoint", "http://domiain.tld/?endpoint")] // Slash inserted by Uri
        [InlineData("http://domiain.tld/", "?endpoint", "http://domiain.tld/?endpoint")]
        [InlineData("http://domiain.tld/with/path", "?endpoint", "http://domiain.tld/with/path?endpoint")]
        [InlineData("http://domiain.tld/with/path/", "?endpoint", "http://domiain.tld/with/path/?endpoint")]
        public void MakeAbsoluteUri_joins_url_with_question_mark(string baseUrl, string endpoint, string expected)
        {
            var rest = new RestClient(null, baseUrl);
            Assert.Equal(expected, rest.MakeAbsoluteUri(endpoint).AbsoluteUri);
        }

        [Fact]
        public void MakeAbsoluteUri_allows_empty_base()
        {
            RestClient rest = new RestClient(null);
            Assert.Equal("http://all.your.base/are/belong/to/us", rest.MakeAbsoluteUri("http://all.your.base/are/belong/to/us").AbsoluteUri);
        }

        [Fact]
        public void MakeAbsoluteUri_allows_empty_endpoint()
        {
            var rest = new RestClient(null, "http://all.your.base/are/belong/to/us");
            Assert.Equal("http://all.your.base/are/belong/to/us", rest.MakeAbsoluteUri("").AbsoluteUri);
        }

        [Fact]
        public void MakeAbsoluteUri_throws_on_invalid_format()
        {
            var rest = new RestClient(null, "not an url");
            Assert.Throws<UriFormatException>(() => rest.MakeAbsoluteUri("not an endpoint"));
        }

        //
        // Request signer
        //

        // Here we test every request type (GET, POST, PUT) only once to make sure all requests are
        // going through a signer. Other signer features we test only on GET to make things brief.

        [Fact]
        public void Get_request_is_signed_with_extra_headers()
        {
            InRequest(
                rest => rest.Get(Url, headers: TestHeaders),
                new AppendSigner(),
                request =>
                {
                    Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName));
                    Assert.Equal([Url], request.Headers.GetValues("TestSigner-uri"));
                    Assert.Equal(["GET"], request.Headers.GetValues("TestSigner-method"));
                    Assert.Equal(["extra"], request.Headers.GetValues("TestSigner-extra"));
                }
            );
        }

        [Fact]
        public void Post_request_is_signed_with_extra_headers()
        {
            InRequest(
                rest => rest.PostJson(Url, RestClient.NoParameters, TestHeaders),
                new AppendSigner(),
                request =>
                {
                    Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName));
                    Assert.Equal([Url], request.Headers.GetValues("TestSigner-uri"));
                    Assert.Equal(["POST"], request.Headers.GetValues("TestSigner-method"));
                    Assert.Equal(["extra"], request.Headers.GetValues("TestSigner-extra"));
                }
            );
        }

        [Fact]
        public void Put_request_is_signed_with_extra_headers()
        {
            InRequest(
                rest => rest.Put(Url, headers: TestHeaders),
                new AppendSigner(),
                request =>
                {
                    Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName));
                    Assert.Equal([Url], request.Headers.GetValues("TestSigner-uri"));
                    Assert.Equal(["PUT"], request.Headers.GetValues("TestSigner-method"));
                    Assert.Equal(["extra"], request.Headers.GetValues("TestSigner-extra"));
                }
            );
        }

        [Fact]
        public void Signer_can_remove_headers()
        {
            InRequest(
                rest => rest.Get(Url, headers: TestHeaders),
                new RemoveSigner(),
                request => Assert.False(request.Headers.Contains(TestHeaderName))
            );
        }

        [Fact]
        public void Signer_can_modify_headers()
        {
            InRequest(
                rest => rest.Get(Url, headers: TestHeaders),
                new ModifySigner(),
                request => Assert.Equal([TestHeaderValue + "-modified"], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Get_sends_default_headers()
        {
            InRequest(
                rest => rest.Get(Url),
                "",
                NoSigner,
                TestHeaders,
                RestClient.NoCookies,
                request => Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Get_sends_default_cookies()
        {
            InRequest(
                rest => rest.Get(Url),
                "",
                NoSigner,
                RestClient.NoHeaders,
                TestCookies,
                request => Assert.Equal([TestCookieHeader], request.Headers.GetValues("Cookie"))
            );
        }

        [Fact]
        public void Post_sends_default_headers()
        {
            InRequest(
                rest => rest.PostJson(Url, RestClient.NoParameters),
                "",
                NoSigner,
                TestHeaders,
                RestClient.NoCookies,
                request => Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Post_sends_default_cookies()
        {
            InRequest(
                rest => rest.PostJson(Url, RestClient.NoParameters),
                "",
                NoSigner,
                RestClient.NoHeaders,
                TestCookies,
                request => Assert.Equal([TestCookieHeader], request.Headers.GetValues("Cookie"))
            );
        }

        [Fact]
        public void Put_sends_default_headers()
        {
            InRequest(
                rest => rest.Put(Url),
                "",
                NoSigner,
                TestHeaders,
                RestClient.NoCookies,
                request => Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Put_sends_default_cookies()
        {
            InRequest(
                rest => rest.Put(Url),
                "",
                NoSigner,
                RestClient.NoHeaders,
                TestCookies,
                request => Assert.Equal([TestCookieHeader], request.Headers.GetValues("Cookie"))
            );
        }

        [Fact]
        public void Request_headers_override_default_headers()
        {
            InRequest(
                rest => rest.Get(Url, headers: TestHeaders),
                "",
                NoSigner,
                new HttpHeaders { [TestHeaderName] = "default-value" },
                RestClient.NoCookies,
                request => Assert.Equal([TestHeaderValue], request.Headers.GetValues(TestHeaderName))
            );
        }

        [Fact]
        public void Request_cookies_override_default_cookies()
        {
            InRequest(
                rest => rest.Get(Url, cookies: TestCookies),
                "",
                NoSigner,
                RestClient.NoHeaders,
                new HttpCookies { [TestCookieName] = "default-value" },
                request => Assert.Equal([TestCookieHeader], request.Headers.GetValues("Cookie"))
            );
        }

        [Fact]
        public void Signer_modifies_default_headers()
        {
            InRequest(
                rest => rest.Get(Url),
                "",
                new ModifySigner(),
                TestHeaders,
                RestClient.NoCookies,
                request => Assert.Equal([TestHeaderValue + "-modified"], request.Headers.GetValues(TestHeaderName))
            );
        }

        //
        // Helpers
        //

        class AppendSigner : IRequestSigner
        {
            public IReadOnlyDictionary<string, string> Sign(
                Uri uri,
                HttpMethod method,
                IReadOnlyDictionary<string, string> headers,
                HttpContent content
            )
            {
                return headers.Merge(
                    new HttpHeaders
                    {
                        ["TestSigner-uri"] = uri.ToString(),
                        ["TestSigner-method"] = method.ToString(),
                        ["TestSigner-extra"] = "extra",
                    }
                );
            }
        }

        class RemoveSigner : IRequestSigner
        {
            public IReadOnlyDictionary<string, string> Sign(
                Uri uri,
                HttpMethod method,
                IReadOnlyDictionary<string, string> headers,
                HttpContent content
            )
            {
                return new HttpHeaders();
            }
        }

        class ModifySigner : IRequestSigner
        {
            public IReadOnlyDictionary<string, string> Sign(
                Uri uri,
                HttpMethod method,
                IReadOnlyDictionary<string, string> headers,
                HttpContent content
            )
            {
                return headers.ToDictionary(x => x.Key, x => x.Value + "-modified");
            }
        }

        // This is for asserting inside a request like this:
        // InRequest(
        //     rest => rest.Get(url),                    // <- perform a rest call
        //     "<html><head>...",                        // <- respond with this content
        //     req => Assert.Equal(url, req.RequestUri)  // <- verify that the request is as expected
        // );
        internal static void InRequest(
            Action<RestClient> restCall,
            string responseContent,
            IRequestSigner signer,
            IReadOnlyDictionary<string, string> defaultHeaders,
            IReadOnlyDictionary<string, string> defaultCookies,
            Action<HttpRequestMessage> assertRequest
        )
        {
            using var transport = new RestTransport(request =>
            {
                assertRequest(request);
                return RespondWith(responseContent, RestClient.NoHeaders)(request);
            });
            restCall(new RestClient(transport, "", signer, defaultHeaders, defaultCookies));
        }

        internal static void InRequest(Action<RestClient> restCall, IRequestSigner signer, Action<HttpRequestMessage> assertRequest)
        {
            InRequest(restCall, "", signer, RestClient.NoHeaders, RestClient.NoCookies, assertRequest);
        }

        internal static void InRequest(Action<RestClient> restCall, Action<HttpRequestMessage> assertRequest)
        {
            InRequest(restCall, NoSigner, assertRequest);
        }

        internal static RestClient Serve(string response, string baseUrl = "")
        {
            return Serve(response, RestClient.NoHeaders, baseUrl);
        }

        internal static RestClient Serve(byte[] response, string baseUrl = "")
        {
            return Serve(response, RestClient.NoHeaders, baseUrl);
        }

        internal static RestClient Serve(string response, IReadOnlyDictionary<string, string> headers, string baseUrl = "")
        {
            return new RestClient(new RestTransport(RespondWith(response, headers)), baseUrl);
        }

        internal static RestClient Serve(byte[] response, IReadOnlyDictionary<string, string> headers, string baseUrl = "")
        {
            return new RestClient(new RestTransport(RespondWith(response, headers)), baseUrl);
        }

        internal static RestClient Fail(HttpStatusCode status, string baseUrl = "")
        {
            return Fail(status, RestClient.NoHeaders, baseUrl);
        }

        internal static RestClient Fail(HttpStatusCode status, IReadOnlyDictionary<string, string> headers, string baseUrl = "")
        {
            return new RestClient(new RestTransport(RespondWith("", headers, status)), baseUrl);
        }

        private static SendAsyncType RespondWith(
            string response,
            IReadOnlyDictionary<string, string> headers,
            HttpStatusCode status = HttpStatusCode.OK
        )
        {
            return RespondWith(new StringContent(response), headers, status);
        }

        private static SendAsyncType RespondWith(
            byte[] response,
            IReadOnlyDictionary<string, string> headers,
            HttpStatusCode status = HttpStatusCode.OK
        )
        {
            var responseContent = new ByteArrayContent(response);
            responseContent.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");

            return RespondWith(responseContent, headers, status);
        }

        private static SendAsyncType RespondWith(
            HttpContent responseContent,
            IReadOnlyDictionary<string, string> headers,
            HttpStatusCode status = HttpStatusCode.OK
        )
        {
            return request =>
            {
                var responseMessage = new HttpResponseMessage(status) { Content = responseContent, RequestMessage = request };

                foreach (var header in headers)
                    responseMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);

                return Task.FromResult(responseMessage);
            };
        }

        //
        // Data
        //

        private const string Url = "https://example.com/";
        private static readonly IRequestSigner NoSigner = null;

        private const string TestHeaderName = "header-name";
        private const string TestHeaderValue = "header-value";

        private static readonly HttpHeaders TestHeaders = new() { [TestHeaderName] = TestHeaderValue };

        private const string TestCookieName = "cookie-name";
        private const string TestCookieValue = "cookie-value";
        private static readonly string TestCookieHeader = $"{TestCookieName}={TestCookieValue}";

        private static readonly HttpCookies TestCookies = new() { [TestCookieName] = TestCookieValue };

        private static readonly HttpHeaders ResponseHeaders =
            new()
            {
                ["ha"] = "ha-ha",
                ["ho"] = "ho-ho",
                ["blah"] = "blah-blah",
            };
    }
}
