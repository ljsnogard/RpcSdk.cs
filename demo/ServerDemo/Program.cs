namespace ServerDemo
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    // https://github.com/commandlineparser/commandline
    using CommandLine;

    using Cysharp.Threading.Tasks;

    [Verb("run", HelpText = "Run a server with specified path and name.")]
    public sealed class RunOptions
    {
        [Option('p', "path", Required = true, HelpText = "The server application path (type.FullName) to run.")]
        public string Path { get; }

        [Option('c', "config", Required = false, HelpText = "The TOML config file path to pass to the application.")]
        public string Config { get; }

        [Option("jsonArgs")]
        public string JsonArgs { get; }

        public RunOptions(string path, string config, string jsonArgs)
        {
            this.Path = path;
            this.Config = config;
            this.JsonArgs = jsonArgs;
        }
    }

    [Verb("list", HelpText = "To list and display information of server application")]
    public sealed class ListOptions
    {
        [Option('p', "path", Required = false, HelpText = "The path of server application of which information to display.")]
        public string Path { get; }

        public ListOptions(string path)
        {
            this.Path = path;
        }
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            Parser.Default
                .ParseArguments<RunOptions, ListOptions>(args)
                .WithParsed<RunOptions>(RunWithOptions)
                .WithParsed<ListOptions>(ListWithOptions)
                .WithNotParsed(HandleErrors);
        }

        private static IEnumerable<System.Type> FindAppTypes()
        {
            return
                from assembly in AppDomain.CurrentDomain.GetAssemblies()
                from t in assembly.GetTypes()
                where typeof(IApp).IsAssignableFrom(t) && (!t.IsInterface) && (!t.IsAbstract) && (!t.IsGenericType)
                select t;
        }

        private static void RunWithOptions(RunOptions options)
        {
            var path = options.Path;
            var candidates =
                from t in FindAppTypes()
                where t.FullName == options.Path
                select t;
            if (candidates.FirstOrDefault() is not System.Type appType)
                throw new ArgumentException($"No type found with Path({options.Path})");
            if (appType.GetConstructor(Array.Empty<Type>()) is not ConstructorInfo ctorInfo)
                throw new ArgumentException($"App \"{options.Path}\" exists but no default constructor available");
            var maybeAppInst = ctorInfo.Invoke(null);
            if (maybeAppInst is not IApp targetApp)
                throw new ArgumentException($"App \"{options.Path}\" exists but the default constructor does not construct an IApp instance");
            targetApp
                .RunAsync(new [] { options.JsonArgs })
                .AsTask()
                .Wait();
        }

        private static void ListWithOptions(ListOptions options)
        {
            var path = string.IsNullOrEmpty(options.Path) ? string.Empty : options.Path;
            var candidates =
                from t in FindAppTypes()
                where !string.IsNullOrEmpty(t.FullName) && t.FullName.Contains(path)
                select t;
            foreach (var t in candidates)
                Console.Out.WriteLine(t.FullName);
        }

        private static void HandleErrors(IEnumerable<CommandLine.Error> errors)
        {
            foreach (var e in errors)
                Console.Error.WriteLine(e.ToString());
        }
    }
}