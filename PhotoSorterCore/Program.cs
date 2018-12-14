using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;


namespace PhotoSorterCore
{
    class Program
    {
        public static IConfigurationRoot Configuration { get; set; }

        static void Main(string[] args)
        {
            var serviceProvider = BuildDi();
            var sorter = serviceProvider.GetService<PhotoSorter>();
            sorter.RunImport();
        }

        private static IServiceProvider BuildDi()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddUserSecrets<ShutterflyAuth>()
                .AddEnvironmentVariables();

            Configuration = builder.Build();
            var services = new ServiceCollection()
                .Configure<ShutterflyAuth>(Configuration.GetSection(nameof(ShutterflyAuth)))
                .Configure<AppConfigs>(Configuration.GetSection(nameof(AppConfigs)))
                .AddOptions()
                .AddTransient<PhotoSorter>();

            return services.BuildServiceProvider();
        }



    }
}
