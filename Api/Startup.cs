﻿using System;
using System.IO;
using System.Reflection;
using Api.Configs;
using Dal.Utilities;
using EFCache;
using EFCache.Redis;
using Logic;
using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Models.Constants;
using Models.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using StackExchange.Redis;
using StructureMap;
using static Api.Utilities.ConnectionStringUtility;

namespace Api
{
    public class Startup
    {
        private Container _container;

        private readonly IWebHostEnvironment _env;

        private readonly IConfigurationRoot _configuration;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="env"></param>
        public Startup(IWebHostEnvironment env)
        {
            _env = env;

            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();

            _configuration = builder.Build();
        }

        /// <summary>
        /// This method gets called by the runtime. Use this method to add services to the container.
        /// For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            var postgresConnectionString =
                ConnectionStringUrlToResource(_configuration.GetValue<string>("DATABASE_URL")
                                              ?? throw new Exception("DATABASE_URL is null"));

            var redisUrl = _configuration.GetValue<string>("REDISTOGO_URL");

            // Add framework services
            // Add functionality to inject IOptions<T>
            services.AddOptions();
            services.Configure<JwtSettings>(_configuration.GetSection("JwtSettings"));

            services.AddLogging();
            
            services.AddRouting(options => { options.LowercaseUrls = true; });

            if (_env.IsDevelopment())
            {
                services.AddDistributedMemoryCache();
            }
            else
            {
                // services.AddStackExchangeRedisCache(c => c.Configuration = redisUrl);
            }

            services.AddSession(options =>
            {
                // Set a short timeout for easy testing.
                options.IdleTimeout = TimeSpan.FromMinutes(50);
                options.Cookie.HttpOnly = true;
                options.Cookie.Name = ApiConstants.AuthenticationSessionCookieName;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo {Title = "Stream-Ripper-API", Version = "v1"});

                // Set the comments path for the Swagger JSON and UI.
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);

                if (File.Exists(xmlPath))
                {
                    c.IncludeXmlComments(xmlPath);
                }
                
                c.AddSecurityDefinition("Bearer", // Name the security scheme
                    new OpenApiSecurityScheme
                    {
                        Description = "JWT Authorization header using the Bearer scheme.",
                        Type = SecuritySchemeType.Http, // We set the scheme type to http since we're using bearer authentication
                        Scheme = "bearer" // The name of the HTTP Authorization scheme to be used in the Authorization header. In this case "bearer".
                    });
            });

            services.AddMvc(x =>
            {
                x.ModelValidatorProviders.Clear();

                // Not need to have https
                x.RequireHttpsPermanent = false;

                // Allow anonymous for localhost
                if (_env.IsDevelopment())
                {
                    x.Filters.Add<AllowAnonymousFilter>();
                }
            }).AddNewtonsoftJson(x =>
            {
                x.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                x.SerializerSettings.Converters.Add(new StringEnumConverter());
            }).AddRazorPagesOptions(x => { x.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute()); });

            services.AddDbContext<EntityDbContext>(opt => opt.UseNpgsql(postgresConnectionString));

            services.AddIdentity<User, IdentityRole<int>>(x => { x.User.RequireUniqueEmail = true; })
                .AddEntityFrameworkStores<EntityDbContext>()
                .AddRoles<IdentityRole<int>>()
                .AddDefaultTokenProviders();

            // L2 EF cache
            if (_env.IsDevelopment())
            {
                EntityFrameworkCache.Initialize(new InMemoryCache());
            }
            else
            {
                var redisConfigurationOptions = ConfigurationOptions.Parse(redisUrl);

                // Important
                redisConfigurationOptions.AbortOnConnectFail = false;

                EntityFrameworkCache.Initialize(new RedisCache(redisConfigurationOptions));
            }

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(x =>
            {
                x.Cookie.MaxAge = TimeSpan.FromMinutes(60);
            });

            _container = new Container(config =>
            {
                config.For<StreamRipperState>().Singleton();
                
                config.For<DocumentStore>().Use(DocumentStore.For(y =>
                {
                    // Important as PLV8 is disabled on Heroku
                    y.PLV8Enabled = false;
                    
                    y.Connection(postgresConnectionString);
                    
                    y.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
                }));

                // Register stuff in container, using the StructureMap APIs...
                config.Scan(_ =>
                {
                    _.AssemblyContainingType(typeof(Startup));
                    _.Assembly("Api");
                    _.Assembly("Logic");
                    _.Assembly("Dal");
                    _.WithDefaultConventions();
                });

                // Populate the container using the service collection
                config.Populate(services);
            });

            _container.AssertConfigurationIsValid();

            return _container.GetInstance<IServiceProvider>();
        }

        /// <summary>
        /// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        /// </summary>
        /// <param name="app"></param>
        public void Configure(IApplicationBuilder app)
        {
            app.UseCors("CorsPolicy");

            if (true || _env.IsDevelopment())
            {
                app.UseDatabaseErrorPage();

                // Enable middleware to serve generated Swagger as a JSON endpoint.
                app.UseSwagger();

                // Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.), 
                // specifying the Swagger JSON endpoint.
                app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1"); });
            }

            // Not necessary for this workshop but useful when running behind kubernetes
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                // Read and use headers coming from reverse proxy: X-Forwarded-For X-Forwarded-Proto
                // This is particularly important so that HttpContent.Request.Scheme will be correct behind a SSL terminating proxy
                ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto
            });

            // Use wwwroot folder as default static path
            app.UseDefaultFiles()
                .UseStaticFiles()
                .UseCookiePolicy()
                .UseSession()
                .UseRouting()
                .UseCors("CorsPolicy")
                .UseAuthentication()
                .UseAuthorization()
                .UseEndpoints(endpoints => endpoints.MapDefaultControllerRoute());

            Console.WriteLine("Application Started!");
        }
    }
}