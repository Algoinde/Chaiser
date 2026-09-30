// Minimal shims so files lifted verbatim from the main AnimeStudio project (which has real
// YAML-export and logging support) compile here without pulling those systems in - this tool
// only ever needs the binary-parsing logic, not YAML dumping, and logs via Console directly.
using System;

namespace Chaiser
{
    public interface IYAMLExportable
    {
        YAMLNode ExportYAML(int[] version);
    }

    public class YAMLNode
    {
    }

    public sealed class YAMLScalarNode : YAMLNode
    {
        public YAMLScalarNode(object value) { }
    }

    public sealed class YAMLMappingNode : YAMLNode
    {
        public MappingStyle Style { get; set; }
        public void Add(string key, object value) { }
        public void Add(string key, YAMLNode value) { }
    }

    public enum MappingStyle
    {
        Block,
        Flow
    }

    public static class Logger
    {
        public static bool VerboseEnabled = false;

        public static void Info(string message) => Console.WriteLine($"[Info] {message}");
        public static void Warning(string message) => Console.WriteLine($"[Warning] {message}");
        public static void Error(string message) => Console.WriteLine($"[Error] {message}");
        public static void Error(string message, Exception e) => Console.WriteLine($"[Error] {message}\n{e}");
        public static void Debug(string message)
        {
            if (VerboseEnabled) Console.WriteLine($"[Debug] {message}");
        }
        public static void Verbose(string message)
        {
            if (VerboseEnabled) Console.WriteLine($"[Verbose] {message}");
        }
    }
}
