﻿namespace Hexalith.AcaAuthentication.Server;

using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;

using Hexalith.Application.Modules.Modules;
using Hexalith.Extensions.Configuration;
using Hexalith.Extensions.Helpers;
using Hexalith.AcaAuthentication.Shared.Configurations;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Validators;

/// <summary>
/// Microsoft Entra ID server module.
/// </summary>
public sealed class HexalithAcaAuthenticationServerModule : IServerApplicationModule
{
    /// <inheritdoc/>
    public IEnumerable<string> Dependencies => [];

    /// <inheritdoc/>
    public string Description => "Microsoft Entra ID server module";

    /// <inheritdoc/>
    public string Id => "Hexalith.AcaAuthentication.Server";

    /// <inheritdoc/>
    public string Name => "Microsoft Entra ID server";

    /// <inheritdoc/>
    public int OrderWeight => 0;

    /// <inheritdoc/>
    public IEnumerable<Assembly> PresentationAssemblies => [GetType().Assembly];

    /// <inheritdoc/>
    public string Version => "1.0";

    /// <inheritdoc/>
    string IApplicationModule.Path => HexalithAcaAuthenticationServerModule.Path;

    private static string CookieScheme => "Cookies";

    private static string AcaAuthenticationScheme => "MicrosoftAcaAuthentication";

    private static string Path => "hexalith/AcaAuthentication";

    /// <summary>
    /// Adds services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddScoped<AuthenticationStateProvider, ServerPersistingAuthenticationStateProvider>();

        AcaAuthenticationSettings settings = configuration.GetSettings<AcaAuthenticationSettings>()
            ?? throw new InvalidOperationException($"Could not load settings section '{AcaAuthenticationSettings.ConfigurationName()}'");
        SettingsException<AcaAuthenticationSettings>.ThrowIfNullOrWhiteSpace(settings.ClientId);
        SettingsException<AcaAuthenticationSettings>.ThrowIfNullOrWhiteSpace(settings.ClientSecret);
        if (settings.AcaAuthenticationType != AcaAuthenticationType.MicrosoftEntraId)
        {
            SettingsException<AcaAuthenticationSettings>.ThrowIfNullOrWhiteSpace(settings.Authority);
        }

        // Add services to the container.
        _ = services.AddAuthentication(AcaAuthenticationScheme)
            .AddOpenIdConnect(AcaAuthenticationScheme, AcaAuthenticationOptions =>
            {
                // The AcaAuthentication handler must use a sign-in scheme capable of persisting
                // user credentials across requests.
                AcaAuthenticationOptions.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

                // The "openid" and "profile" scopes are required for the AcaAuthentication handler
                // and included by default. You should enable these scopes here if scopes
                // are provided by "Authentication:Schemes:MicrosoftAcaAuthentication:Scope"
                // configuration because configuration may overwrite the scopes collection.
                AcaAuthenticationOptions.Scope.Add(OpenIdConnectScope.OpenIdProfile);

                // SaveTokens is set to false by default because tokens aren't required
                // by the app to make additional external API requests.
                AcaAuthenticationOptions.SaveTokens = false;

                // The following paths must match the redirect and post logout redirect
                // paths configured when registering the application with the AcaAuthentication provider.
                // For Microsoft Entra ID, this is accomplished through the "Authentication"
                // blade of the application's registration in the Azure portal. Both the
                // signin and signout paths must be registered as Redirect URIs. The default
                // values are "/signin-AcaAuthentication" and "/signout-callback-AcaAuthentication".
                // Microsoft Identity currently only redirects back to the
                // SignedOutCallbackPath if authority is
                // https://login.microsoftonline.com/{TENANT ID}/v2.0/ as it is above.
                // You can use the "common" authority instead, and logout redirects back to
                // the Blazor app. For more information, see
                // https://github.com/AzureAD/microsoft-authentication-library-for-js/issues/5783
                AcaAuthenticationOptions.CallbackPath = new PathString("/signin-AcaAuthentication");
                AcaAuthenticationOptions.SignedOutCallbackPath = new PathString("/signout-callback-AcaAuthentication");

                // The RemoteSignOutPath is the "Front-channel logout URL" for remote single
                // sign-out. The default value is "/signout-AcaAuthentication".
                AcaAuthenticationOptions.RemoteSignOutPath = new PathString("/signout-AcaAuthentication");

                // The "offline_access" scope is required for the refresh token.
                AcaAuthenticationOptions.Scope.Add(OpenIdConnectScope.OfflineAccess);

                string tenant = string.IsNullOrWhiteSpace(settings.Tenant) ? "common" : settings.Tenant;
                AcaAuthenticationOptions.Authority = settings.AcaAuthenticationType == AcaAuthenticationType.MicrosoftEntraId
                    ? $"https://login.microsoftonline.com/{tenant}/v2.0/"
                    : settings.Authority;

                // Set the client identifier and secret for the app.
                AcaAuthenticationOptions.ClientId = settings.ClientId;
                AcaAuthenticationOptions.ClientSecret = settings.ClientSecret;

                // Setting ResponseType to "code" configures the AcaAuthentication handler to use
                // authorization code flow. Implicit grants and hybrid flows are unnecessary
                // in this mode. In a Microsoft Entra ID app registration, you don't need to
                // select either box for the authorization endpoint to return access tokens
                // or ID tokens. The AcaAuthentication handler automatically requests the appropriate
                // tokens using the code returned from the authorization endpoint.
                AcaAuthenticationOptions.ResponseType = OpenIdConnectResponseType.Code;

                // Many AcaAuthentication servers use "name" and "role" rather than the SOAP/WS-Fed
                // defaults in ClaimTypes. If you don't use ClaimTypes, mapping inbound
                // claims to ASP.NET Core's ClaimTypes isn't necessary.
                AcaAuthenticationOptions.MapInboundClaims = false;
                AcaAuthenticationOptions.TokenValidationParameters.NameClaimType = JwtRegisteredClaimNames.Name;
                AcaAuthenticationOptions.TokenValidationParameters.RoleClaimType = "role";

                if (settings.AcaAuthenticationType == AcaAuthenticationType.MicrosoftEntraId && string.IsNullOrWhiteSpace(settings.Tenant))
                {
                    // Many AcaAuthentication providers work with the default issuer validator, but the
                    // configuration must account for the issuer parameterized with "{TENANT ID}"
                    // returned by the "common" endpoint's /.well-known/openid-configuration
                    // For more information, see
                    // https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/issues/1731
                    AadIssuerValidator microsoftIssuerValidator = AadIssuerValidator.GetAadIssuerValidator(AcaAuthenticationOptions.Authority);
                    AcaAuthenticationOptions.TokenValidationParameters.IssuerValidator = microsoftIssuerValidator.Validate;
                }
            })
            .AddCookie(CookieScheme);

        // This attaches a cookie OnValidatePrincipal callback to get a new access token when the current one expires, and
        // reissue a cookie with the new access token saved inside. If the refresh fails, the user will be signed out.
        _ = services.ConfigureCookieAcaAuthenticationRefresh(CookieScheme, AcaAuthenticationScheme);

        _ = services.AddAuthorization();
    }

    /// <inheritdoc/>
    public void UseModule(object builder)
    {
        if (builder is not IEndpointRouteBuilder endpoints)
        {
            throw new ArgumentNullException(nameof(builder), $"The application object does not implement {nameof(IEndpointRouteBuilder)}.");
        }

        RouteGroupBuilder group = endpoints.MapGroup(Path);

        _ = group.MapGet("login", (string? returnUrl) => TypedResults.Challenge(GetAuthProperties(returnUrl)))
                .AllowAnonymous();

        // Sign out of the Cookie and AcaAuthentication handlers. If you do not sign out with the AcaAuthentication handler,
        // the user will automatically be signed back in the next time they visit a page that requires authentication
        // without being able to choose another account.
        _ = group.MapPost("logout", ([FromForm] string? returnUrl) => TypedResults.SignOut(
                GetAuthProperties(returnUrl),
                [CookieScheme, AcaAuthenticationScheme]));
    }

    private static AuthenticationProperties GetAuthProperties(string? returnUrl)
    {
        // TODO: Use HttpContext.Request.PathBase instead.
        const string pathBase = "/";

        // Prevent open redirects.
        if (string.IsNullOrEmpty(returnUrl))
        {
            returnUrl = pathBase;
        }
        else if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            returnUrl = new Uri(returnUrl, UriKind.Absolute).PathAndQuery;
        }
        else if (returnUrl[0] != '/')
        {
            returnUrl = $"{pathBase}{returnUrl}";
        }

        return new AuthenticationProperties { RedirectUri = returnUrl };
    }
}