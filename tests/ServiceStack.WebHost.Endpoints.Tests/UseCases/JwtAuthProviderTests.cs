﻿using System;
using System.Reflection;
using System.Threading.Tasks;
using Funq;
using NUnit.Framework;
using ServiceStack;
using ServiceStack.Text;
using ServiceStack.Auth;
using ServiceStack.Caching;
using ServiceStack.Data;
using ServiceStack.OrmLite;

namespace ServiceStack.WebHost.Endpoints.Tests.UseCases
{
    public class HelloJwt : IReturn<HelloJwtResponse>
    {
        public string Name { get; set; }
    }
    public class HelloJwtResponse
    {
        public string Result { get; set; }
    }

    [Authenticate]
    public class JwtServices : Service
    {
        public object Any(HelloJwt request)
        {
            return new HelloJwtResponse { Result = $"Hello, {request.Name}" };
        }
    }

    public class JwtAuthProviderRsaEncryptedTests : JwtAuthProviderTests
    {
        protected override JwtAuthProvider CreateJwtAuthProvider()
        {
            var privateKey = RsaUtils.CreatePrivateKeyParams(RsaKeyLengths.Bit2048);
            var publicKey = privateKey.ToPublicRsaParameters();
            var privateKeyXml = privateKey.ToPrivateKeyXml();
            var publicKeyXml = privateKey.ToPublicKeyXml();

            return new JwtAuthProvider
            {
                HashAlgorithm = "RS256",
                PrivateKeyXml = privateKeyXml,
                EncryptPayload = true,
                RequireSecureConnection = false,
            };
        }
    }

    public class JwtAuthProviderRsaTests : JwtAuthProviderTests
    {
        protected override JwtAuthProvider CreateJwtAuthProvider()
        {
            var privateKey = RsaUtils.CreatePrivateKeyParams(RsaKeyLengths.Bit2048);
            var publicKey = privateKey.ToPublicRsaParameters();
            var privateKeyXml = privateKey.ToPrivateKeyXml();
            var publicKeyXml = privateKey.ToPublicKeyXml();

            return new JwtAuthProvider
            {
                HashAlgorithm = "RS256",
                PrivateKeyXml = privateKeyXml,
                RequireSecureConnection = false,
            };
        }
    }

    public class JwtAuthProviderHS256Tests : JwtAuthProviderTests
    {
        protected override JwtAuthProvider CreateJwtAuthProvider()
        {
            return new JwtAuthProvider
            {
                AuthKey = AesUtils.CreateKey(),
                RequireSecureConnection = false,
            };
        }
    }

    public class JwtAuthProviderHS256HttpClientTests : JwtAuthProviderHS256Tests
    {
        protected override IJsonServiceClient GetClient()
        {
            return new JsonHttpClient(Config.ListeningOn);
        }

        protected override IJsonServiceClient GetClientWithCredentials()
        {
            return new JsonHttpClient(Config.ListeningOn)
            {
                UserName = Username,
                Password = Password,
            };
        }
    }

    public abstract class JwtAuthProviderTests
    {
        public const string Username = "mythz";
        public const string Password = "p@55word";

        class AppHost : AppSelfHostBase
        {
            public AppHost()
                : base(nameof(JwtAuthProviderTests), typeof(JwtServices).GetAssembly()) { }

            public virtual JwtAuthProvider JwtAuthProvider { get; set; }

            public override void Configure(Container container)
            {
                var dbFactory = new OrmLiteConnectionFactory(":memory:", SqliteDialect.Provider);

                container.Register<IDbConnectionFactory>(dbFactory);
                container.Register<IAuthRepository>(c =>
                    new OrmLiteAuthRepository(c.Resolve<IDbConnectionFactory>())
                    {
                        UseDistinctRoleTables = true
                    });

                //Create UserAuth RDBMS Tables
                container.Resolve<IAuthRepository>().InitSchema();

                //Also store User Sessions in SQL Server
                container.RegisterAs<OrmLiteCacheClient, ICacheClient>();
                container.Resolve<ICacheClient>().InitSchema();

                // just for testing, create a privateKeyXml on every instance
                Plugins.Add(new AuthFeature(() => new AuthUserSession(),
                    new IAuthProvider[]
                    {
                        JwtAuthProvider,
                        new CredentialsAuthProvider()
                    }));


                Plugins.Add(new RegistrationFeature());

                var authRepo = GetAuthRepository();
                authRepo.CreateUserAuth(new UserAuth
                {
                    Id = 1,
                    UserName = Username,
                    FirstName = "First",
                    LastName = "Last",
                    DisplayName = "Display",
                }, Password);
            }
        }

        protected abstract JwtAuthProvider CreateJwtAuthProvider();

        protected virtual IJsonServiceClient GetClient() => new JsonServiceClient(Config.ListeningOn);

        protected virtual IJsonServiceClient GetClientWithCredentials() => new JsonServiceClient(Config.ListeningOn)
        {
            UserName = Username,
            Password = Password
        };

        protected virtual IJsonServiceClient GetClientWithRefreshToken(string refreshToken = null, string accessToken = null)
        {
            if (refreshToken == null)
            {
                refreshToken = GetRefreshToken();
            }

            var client = GetClient();
            var serviceClient = client as JsonServiceClient;
            if (serviceClient != null)
            {
                serviceClient.BearerToken = accessToken;
                serviceClient.RefreshToken = refreshToken;
                return serviceClient;
            }

            var httpClient = client as JsonHttpClient;
            if (httpClient != null)
            {
                httpClient.BearerToken = accessToken;
                httpClient.RefreshToken = refreshToken;
                return httpClient;
            }

            throw new NotSupportedException(client.GetType().Name);
        }

        private static string CreateExpiredToken()
        {
            var jwtProvider = (JwtAuthProvider)AuthenticateService.GetAuthProvider(JwtAuthProvider.Name);
            jwtProvider.CreatePayloadFilter = (jwtPayload, session) =>
                jwtPayload["exp"] = DateTime.UtcNow.AddSeconds(-1).ToUnixTime().ToString();

            var token = jwtProvider.CreateJwtBearerToken(new AuthUserSession
            {
                UserAuthId = "1",
                DisplayName = "Test",
                Email = "as@if.com"
            });

            jwtProvider.CreatePayloadFilter = null;
            return token;
        }

        private readonly ServiceStackHost appHost;

        public JwtAuthProviderTests()
        {
            appHost = new AppHost
                {
                    JwtAuthProvider = CreateJwtAuthProvider()
                }
                .Init()
                .Start(Config.ListeningOn);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown() => appHost.Dispose();

        private string GetRefreshToken()
        {
            var authClient = GetClient();
            var refreshToken = authClient.Send(new Authenticate
                {
                    provider = "credentials",
                    UserName = Username,
                    Password = Password,
                })
                .RefreshToken;
            return refreshToken;
        }

        [Test]
        public void Can_get_TokenCookie()
        {
            var authClient = GetClient();
            var authResponse = authClient.Send(new Authenticate
            {
                provider = "credentials",
                UserName = Username,
                Password = Password,
                UseTokenCookie = true
            });
            Assert.That(authResponse.SessionId, Is.Not.Null);
            Assert.That(authResponse.UserName, Is.EqualTo(Username));

            var response = authClient.Send(new HelloJwt { Name = "from auth service" });
            Assert.That(response.Result, Is.EqualTo("Hello, from auth service"));

            var jwtToken = authClient.GetTokenCookie(); //From ss-tok Cookie
            Assert.That(jwtToken, Is.Not.Null);
        }

        [Test]
        public void Can_ConvertSessionToToken()
        {
            var authClient = GetClient();
            var authResponse = authClient.Send(new Authenticate
            {
                provider = "credentials",
                UserName = Username,
                Password = Password,
                RememberMe = true,
            });
            Assert.That(authResponse.SessionId, Is.Not.Null);
            Assert.That(authResponse.UserName, Is.EqualTo(Username));
            Assert.That(authResponse.BearerToken, Is.Not.Null);

            var jwtToken = authClient.GetTokenCookie(); //From ss-tok Cookie
            Assert.That(jwtToken, Is.Null);

            var response = authClient.Send(new HelloJwt { Name = "from auth service" });
            Assert.That(response.Result, Is.EqualTo("Hello, from auth service"));

            authClient.Send(new ConvertSessionToToken());
            jwtToken = authClient.GetTokenCookie(); //From ss-tok Cookie
            Assert.That(jwtToken, Is.Not.Null);

            response = authClient.Send(new HelloJwt { Name = "from auth service" });
            Assert.That(response.Result, Is.EqualTo("Hello, from auth service"));
        }

        [Test]
        public void Invalid_RefreshToken_throws_RefreshTokenException()
        {
            var client = GetClientWithRefreshToken("Invalid.Refresh.Token");
            try
            {
                var request = new Secured { Name = "test" };
                var response = client.Send(request);
                Assert.Fail("Should throw");
            }
            catch (RefreshTokenException ex)
            {
                ex.Message.Print();
                Assert.That(ex.StatusCode, Is.EqualTo(400));
                Assert.That(ex.ErrorCode, Is.EqualTo(nameof(ArgumentException)));
            }
        }

        [Test]
        public async Task Invalid_RefreshToken_throws_RefreshTokenException_Async()
        {
            var client = GetClientWithRefreshToken("Invalid.Refresh.Token");
            try
            {
                var request = new Secured { Name = "test" };
                var response = await client.SendAsync(request);
                Assert.Fail("Should throw");
            }
            catch (RefreshTokenException ex)
            {
                ex.Message.Print();
                Assert.That(ex.StatusCode, Is.EqualTo(400));
                Assert.That(ex.ErrorCode, Is.EqualTo(nameof(ArgumentException)));
            }
        }

        [Test]
        public void Only_returns_Tokens_on_Requests_that_Authenticate_the_user()
        {
            var authClient = GetClient();
            var refreshToken = authClient.Send(new Authenticate
            {
                provider = "credentials",
                UserName = Username,
                Password = Password,
            }).RefreshToken;

            Assert.That(refreshToken, Is.Not.Null); //On Auth using non IAuthWithRequest

            var postAuthRefreshToken = authClient.Send(new Authenticate()).RefreshToken;
            Assert.That(postAuthRefreshToken, Is.Null); //After Auth
        }

        [Test]
        public void Can_Auto_reconnect_with_just_RefreshToken()
        {
            var client = GetClientWithRefreshToken();

            var request = new Secured { Name = "test" };
            var response = client.Send(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));

            response = client.Send(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));
        }

        [Test]
        public void Can_Auto_reconnect_with_RefreshToken_after_expired_token()
        {
            var client = GetClientWithRefreshToken(GetRefreshToken(), CreateExpiredToken());

            var request = new Secured { Name = "test" };
            var response = client.Send(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));

            response = client.Send(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));
        }

        [Test]
        public async Task Can_Auto_reconnect_with_RefreshToken_after_expired_token_Async()
        {
            var client = GetClientWithRefreshToken(GetRefreshToken(), CreateExpiredToken());

            var request = new Secured { Name = "test" };
            var response = await client.SendAsync(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));

            response = await client.SendAsync(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));
        }

        [Test]
        public void Can_Auto_reconnect_with_RefreshToken_in_OnAuthenticationRequired_after_expired_token()
        {
            var client = GetClient();
            var serviceClient = client as JsonServiceClient;
            if (serviceClient == null) //OnAuthenticationRequired not implemented in JsonHttpClient
                return;

            var called = 0;
            serviceClient.BearerToken = CreateExpiredToken();

            serviceClient.OnAuthenticationRequired = () =>
            {
                called++;
                var authClient = GetClient();
                serviceClient.BearerToken = authClient.Send(new GetAccessToken
                {
                    RefreshToken = GetRefreshToken(),
                }).AccessToken;
            };

            var request = new Secured { Name = "test" };
            var response = client.Send(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));

            response = client.Send(request);
            Assert.That(response.Result, Is.EqualTo(request.Name));

            Assert.That(called, Is.EqualTo(1));
        }

        [Test]
        public void Does_return_token_on_subsequent_BasicAuth_Authentication_requests()
        {
            var client = GetClientWithCredentials();

            var response = client.Post(new Authenticate());
            Assert.That(response.BearerToken, Is.Not.Null);
            Assert.That(response.RefreshToken, Is.Not.Null);

            response = client.Post(new Authenticate());
            Assert.That(response.BearerToken, Is.Not.Null);
            Assert.That(response.RefreshToken, Is.Not.Null);
        }
    }
}