
using Microsoft.Extensions.Configuration;
using System.CommandLine;
using System.IO;

var configOption = new Option<string>("--config")
{
    Arity = ArgumentArity.ZeroOrOne,
    Description = "Path to the configuration file"
};

var rootCommand = SFTPSync.SFTPSync.GetRootCommand();
rootCommand.Options.Add(configOption);

// Parse args to get config path
var tempParseResult = rootCommand.Parse(args);

var configPath = tempParseResult.GetValue(configOption) ?? "sftpsyncsettings.json";
if (!Path.IsPathRooted(configPath))
{
    configPath = Path.Combine(AppContext.BaseDirectory, configPath);
}

var config = new ConfigurationBuilder()
    .AddJsonFile(configPath, optional: true)
    .Build();

// Now create the real rootCommand with config
rootCommand = SFTPSync.SFTPSync.GetRootCommand(config);
rootCommand.Options.Add(configOption);
var parseResult = rootCommand.Parse(args);
await parseResult.InvokeAsync();
