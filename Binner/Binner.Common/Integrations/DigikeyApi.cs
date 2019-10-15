﻿using ApiClient.OAuth2;
using Binner.Common.Integrations.Models.Digikey;
using Binner.Common.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Binner.Common.Integrations
{
    public class DigikeyApi
    {
        public static readonly TimeSpan MaxAuthorizationWaitTime = TimeSpan.FromSeconds(30);
        public const string Path = "https://sandbox-api.digikey.com/Search/v3/Products";
        private readonly OAuth2Service _oAuth2Service;
        private readonly ICredentialService _credentialService;
        private readonly HttpClient _client;
        private readonly ManualResetEvent _manualResetEvent = new ManualResetEvent(false);
        private readonly JsonSerializerSettings _serializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            // ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };

        public DigikeyApi(OAuth2Service oAuth2Service, ICredentialService credentialService)
        {
            _oAuth2Service = oAuth2Service;
            _credentialService = credentialService;
            _client = new HttpClient();
        }

        public async Task<ICollection<Product>> GetProductInformationAsync(string partNumber)
        {
            var authResponse = await AuthorizeAsync();
            if (authResponse == null || !authResponse.IsAuthorized) throw new UnauthorizedAccessException("Unable to authenticate with DigiKey");

            return await WrapApiRequestAsync<ICollection<Product>>(async () =>
            {
                try
                {
                    // set what fields we want from the API
                    var includes = new List<string> { "DigiKeyPartNumber", "QuantityAvailable", "Manufacturer", "ManufacturerPartNumber", "PrimaryDatasheet", "ProductDescription", "DetailedDescription", "MinimumOrderQuantity", "NonStock", "UnitPrice", "ProductStatus", "ProductUrl", "Parameters" };
                    var values = new Dictionary<string, string>
                    {
                        { "Includes", $"Products({string.Join(",", includes)})" },
                    };
                    var uri = new Uri($"{Path}/Keyword?" + string.Join("&", values.Select(x => $"{x.Key}={x.Value}")));
                    var requestMessage = CreateRequest(authResponse, HttpMethod.Post, uri);
                    var request = new KeywordSearchRequest
                    {
                        Keywords = partNumber
                    };
                    var json = JsonConvert.SerializeObject(request, _serializerSettings);
                    requestMessage.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    // perform a keywords API search
                    var response = await _client.SendAsync(requestMessage);
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        throw new UnauthorizedException(authResponse);
                    if (response.IsSuccessStatusCode)
                    {
                        var resultString = response.Content.ReadAsStringAsync().Result;
                        var results = JsonConvert.DeserializeObject<KeywordSearchResponse>(resultString, _serializerSettings);
                        return results.Products;
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return new List<Product>();
            });

        }

        /// <summary>
        /// Wraps an API request - if the request is unauthorized it will refresh the Auth token and re-issue the request
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="func"></param>
        /// <returns></returns>
        private async Task<T> WrapApiRequestAsync<T>(Func<Task<T>> func)
        {
            try
            {
                return await func();
            }
            catch (UnauthorizedException ex)
            {
                // get refresh token, retry
                _oAuth2Service.ClientSettings.RefreshToken = ex.Authorization.RefreshToken;
                var token = await _oAuth2Service.RefreshTokenAsync();
                var authRequest = new DigikeyAuthorization(_oAuth2Service.ClientSettings.ClientId)
                {
                    AccessToken = token.AccessToken,
                    RefreshToken = token.RefreshToken,
                    CreatedUtc = DateTime.UtcNow,
                    AuthorizationReceived = true,
                };
                ServerContext.Set(nameof(DigikeyAuthorization), authRequest);
                if (authRequest.IsAuthorized)
                {
                    // save the credential
                    await _credentialService.SaveOAuthCredentialAsync(new Common.Models.OAuthCredential
                    {
                        Provider = nameof(DigikeyApi),
                        AccessToken = authRequest.AccessToken,
                        RefreshToken = authRequest.RefreshToken,
                        DateCreatedUtc = authRequest.CreatedUtc,
                        DateExpiresUtc = authRequest.ExpiresUtc,
                    });
                    // call the API again
                    return await func();
                }
                // refresh token failed, restart access token retrieval process
                await ForgetAuthenticationTokens();
                await AuthorizeAsync();
                // call the API again
                return await func();
            }
        }

        private async Task ForgetAuthenticationTokens()
        {
            ServerContext.Remove<DigikeyAuthorization>(nameof(DigikeyAuthorization));
            await _credentialService.RemoveOAuthCredentialAsync(nameof(DigikeyApi));
        }

        private async Task<DigikeyAuthorization> AuthorizeAsync()
        {
            // check if we have an in-memory auth credential
            var getAuth = ServerContext.Get<DigikeyAuthorization>(nameof(DigikeyAuthorization));
            if (getAuth != null && getAuth.IsAuthorized)
                return getAuth;

            // check if we have a saved to disk auth credential
            var credential = await _credentialService.GetOAuthCredentialAsync(nameof(DigikeyApi));
            if (credential == null)
            {
                // request a token if we don't already have one
                var scopes = "";
                var authUrl = _oAuth2Service.GenerateAuthUrl(scopes);
                OpenBrowser(authUrl);
                var authRequest = new DigikeyAuthorization(_oAuth2Service.ClientSettings.ClientId);
                ServerContext.Set(nameof(DigikeyAuthorization), authRequest);

                // wait for oAuth callback authorization from Digikey or timeout
                var startTime = DateTime.Now;
                while (!_manualResetEvent.WaitOne(100))
                {
                    getAuth = ServerContext.Get<DigikeyAuthorization>(nameof(DigikeyAuthorization));
                    if (getAuth.AuthorizationReceived)
                    {
                        // ok, it either failed or succeeded
                        if (getAuth.IsAuthorized)
                        {
                            // save the credential
                            await _credentialService.SaveOAuthCredentialAsync(new Common.Models.OAuthCredential
                            {
                                Provider = nameof(DigikeyApi),
                                AccessToken = getAuth.AccessToken,
                                RefreshToken = getAuth.RefreshToken,
                                DateCreatedUtc = getAuth.CreatedUtc,
                                DateExpiresUtc = getAuth.ExpiresUtc,
                            });
                        }
                        return getAuth;
                    }
                    else
                    {
                        if (DateTime.Now.Subtract(startTime) >= MaxAuthorizationWaitTime)
                        {
                            // timeout
                            return null;
                        }
                    }
                }
            }
            else
            {
                // reuse a saved oAuth credential
                var authRequest = new DigikeyAuthorization(_oAuth2Service.ClientSettings.ClientId)
                {
                    AccessToken = credential.AccessToken,
                    RefreshToken = credential.RefreshToken,
                    CreatedUtc = credential.DateCreatedUtc,
                    ExpiresUtc = credential.DateExpiresUtc,
                    AuthorizationReceived = true,
                };
                // also store it in memory
                ServerContext.Set(nameof(DigikeyAuthorization), authRequest);
                return authRequest;
            }

            return null;
        }

        private HttpRequestMessage CreateRequest(DigikeyAuthorization authResponse, HttpMethod method, Uri uri)
        {
            var message = new HttpRequestMessage(method, uri);
            message.Headers.Add("X-DIGIKEY-Client-Id", authResponse.ClientId);
            message.Headers.Add("Authorization", $"Bearer {authResponse.AccessToken}");
            message.Headers.Add("X-DIGIKEY-Locale-Site", "CA");
            message.Headers.Add("X-DIGIKEY-Locale-Language", "en");
            message.Headers.Add("X-DIGIKEY-Locale-Currency", "CAD");
            return message;
        }

        private void OpenBrowser(string url)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}")); // Works ok on windows and escape need for cmd.exe
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);  // Works ok on linux
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url); // Not tested
            }
            else
                throw new InvalidOperationException("Failed to launch default web browser - I don't know how to do this on your platform!");
        }

    }

    public class UnauthorizedException : Exception
    {
        public DigikeyAuthorization Authorization { get; }
        public UnauthorizedException(DigikeyAuthorization authorization)
        {
            Authorization = authorization;
        }
    }
}