//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2019 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ArchiSteamFarm.IPC.Integration;
using ArchiSteamFarm.Plugins;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ArchiSteamFarm.IPC {
	internal sealed class Startup {
		private readonly IConfiguration Configuration;

		public Startup([NotNull] IConfiguration configuration) => Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

		public void Configure(IApplicationBuilder app, IHostingEnvironment env) {
			if ((app == null) || (env == null)) {
				ASF.ArchiLogger.LogNullError(nameof(app) + " || " + nameof(env));

				return;
			}

			// The order of dependency injection matters, pay attention to it

			// Add workaround for missing PathBase feature, https://github.com/aspnet/Hosting/issues/1120
			PathString pathBase = Configuration.GetSection("Kestrel").GetValue<PathString>("PathBase");

			if (!string.IsNullOrEmpty(pathBase) && (pathBase != "/")) {
				app.UsePathBase(pathBase);
			}

			// Add support for proxies
			app.UseForwardedHeaders();

			// Add support for response compression
			app.UseResponseCompression();

			if (!string.IsNullOrEmpty(ASF.GlobalConfig.IPCPassword)) {
				// We need ApiAuthenticationMiddleware for IPCPassword
				app.UseWhen(context => context.Request.Path.StartsWithSegments("/Api", StringComparison.OrdinalIgnoreCase), appBuilder => appBuilder.UseMiddleware<ApiAuthenticationMiddleware>());

				// We want to apply CORS policy in order to allow userscripts and other third-party integrations to communicate with ASF API
				// We apply CORS policy only with IPCPassword set as extra authentication measure
				app.UseCors();
			}

			// We need WebSockets support for /Api/Log
			app.UseWebSockets();

			// We need MVC for /Api
			app.UseMvcWithDefaultRoute();

			// Use swagger for automatic API documentation generation
			app.UseSwagger();

			// Use friendly swagger UI
			app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/" + SharedInfo.ASF + "/swagger.json", SharedInfo.ASF + " API"));

			// We're using index for URL routing in our static files so re-execute all non-API calls on /
			app.UseWhen(context => !context.Request.Path.StartsWithSegments("/Api", StringComparison.OrdinalIgnoreCase), appBuilder => appBuilder.UseStatusCodePagesWithReExecute("/"));

			// We need static files support for IPC GUI
			app.UseDefaultFiles();
			app.UseStaticFiles();
		}

		public void ConfigureServices(IServiceCollection services) {
			if (services == null) {
				ASF.ArchiLogger.LogNullError(nameof(services));

				return;
			}

			// The order of dependency injection matters, pay attention to it

			// Add support for proxies
			services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

			// Add support for response compression
			services.AddResponseCompression();

			// Add CORS to allow userscripts and third-party apps
			services.AddCors(builder => builder.AddDefaultPolicy(policyBuilder => policyBuilder.AllowAnyOrigin()));

			// Add swagger documentation generation
			services.AddSwaggerGen(
				c => {
					c.AddSecurityDefinition(
						nameof(GlobalConfig.IPCPassword), new OpenApiSecurityScheme {
							Description = nameof(GlobalConfig.IPCPassword) + " authentication using request headers. Check " + SharedInfo.ProjectURL + "/wiki/IPC#authentication for more info.",
							In = ParameterLocation.Header,
							Name = ApiAuthenticationMiddleware.HeadersField,
							Type = SecuritySchemeType.ApiKey
						}
					);

					c.AddSecurityRequirement(
						new OpenApiSecurityRequirement {
							{
								new OpenApiSecurityScheme {
									Reference = new OpenApiReference {
										Id = nameof(GlobalConfig.IPCPassword),
										Type = ReferenceType.SecurityScheme
									}
								},

								new string[0]
							}
						}
					);

					c.DescribeAllEnumsAsStrings();
					c.EnableAnnotations();

					c.SwaggerDoc(
						SharedInfo.ASF, new OpenApiInfo {
							Contact = new OpenApiContact {
								Name = SharedInfo.GithubRepo,
								Url = new Uri(SharedInfo.ProjectURL)
							},

							License = new OpenApiLicense {
								Name = SharedInfo.LicenseName,
								Url = new Uri(SharedInfo.LicenseURL)
							},

							Title = SharedInfo.ASF + " API"
						}
					);

					string xmlDocumentationFile = Path.Combine(AppContext.BaseDirectory, SharedInfo.AssemblyDocumentation);

					if (File.Exists(xmlDocumentationFile)) {
						c.IncludeXmlComments(xmlDocumentationFile);
					}
				}
			);

			// We need MVC for /Api, but we're going to use only a small subset of all available features
			IMvcCoreBuilder mvc = services.AddMvcCore();

			// Add support for controllers declared in custom plugins
			HashSet<Assembly> assemblies = PluginsCore.LoadAssemblies();

			if (assemblies != null) {
				foreach (Assembly assembly in assemblies) {
					mvc.AddApplicationPart(assembly);
				}
			}

			// Add API explorer for swagger
			mvc.AddApiExplorer();

			// Use latest compatibility version for MVC
			mvc.SetCompatibilityVersion(CompatibilityVersion.Latest);

			// Add standard formatters that can be used for serializing/deserializing requests/responses, they're already available in the core
			mvc.AddFormatterMappings();

			// Add JSON formatters that will be used as default ones if no specific formatters are asked for
			mvc.AddJsonFormatters();

			// Fix default contract resolver to use original names and not a camel case
			// Also add debugging aid while we're at it
			mvc.AddJsonOptions(
				options => {
					options.SerializerSettings.ContractResolver = new DefaultContractResolver();

					if (Debugging.IsUserDebugging) {
						options.SerializerSettings.Formatting = Formatting.Indented;
					}
				}
			);
		}
	}
}
