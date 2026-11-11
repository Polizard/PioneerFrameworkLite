using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.MapGeneration
{
    public enum MapGenerationStage { Fields, NaturalSurface, Content, Populate }
    public enum MapGenerationPurpose { Colony, Landing, Encounter, Quest }
    public enum MapGenerationDomain { Natural, Purpose }

    /// <summary>Frozen source identity and numeric/tag facts; native session objects belong in services.</summary>
    public sealed class MapSourceContext
    {
        public string SourceId { get; }
        public ulong GenerationSeed { get; }
        public int TileId { get; }
        public int SizeX { get; }
        public int SizeZ { get; }
        public string BiomeId { get; }
        public IReadOnlyDictionary<string, double> NumericData { get; }
        public IReadOnlyDictionary<string, string> Tags { get; }

        public MapSourceContext(string sourceId, ulong generationSeed, int tileId, int sizeX, int sizeZ,
            string biomeId, IReadOnlyDictionary<string, double>? numericData = null,
            IReadOnlyDictionary<string, string>? tags = null)
        {
            SourceId = MapGenerationGuards.Identity(sourceId);
            BiomeId = MapGenerationGuards.Identity(biomeId);
            ArgumentOutOfRangeException.ThrowIfNegative(tileId);
            if (sizeX <= 0 || sizeZ <= 0) throw new ArgumentOutOfRangeException(nameof(sizeX));
            GenerationSeed = generationSeed;
            TileId = tileId;
            SizeX = sizeX;
            SizeZ = sizeZ;
            var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in numericData ?? new Dictionary<string, double>())
            {
                if (!double.IsFinite(pair.Value)) throw new ArgumentException("Numeric metadata must be finite.", nameof(numericData));
                numbers.Add(MapGenerationGuards.Identity(pair.Key), pair.Value);
            }
            NumericData = new ReadOnlyDictionary<string, double>(numbers);
            var copiedTags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in tags ?? new Dictionary<string, string>())
                copiedTags.Add(MapGenerationGuards.Identity(pair.Key), pair.Value ?? throw new ArgumentException("Null tag.", nameof(tags)));
            Tags = new ReadOnlyDictionary<string, string>(copiedTags);
        }
    }

    /// <summary>Name is module-local; Key binds that port to a session resource.</summary>
    public sealed class MapGenerationPort
    {
        public string Name { get; }
        public string Key { get; }
        public string TypeId { get; }
        public MapGenerationPort(string key, string typeId) : this(key, key, typeId) { }
        public MapGenerationPort(string name, string key, string typeId)
        {
            Name = MapGenerationGuards.Identity(name);
            Key = MapGenerationGuards.Identity(key);
            TypeId = MapGenerationGuards.Identity(typeId);
        }
    }

    public sealed class MapGenerationPortContract
    {
        public string Name { get; }
        public string TypeId { get; }
        public Type RuntimeType { get; }
        public MapGenerationPortContract(string name, string typeId, Type runtimeType)
        {
            Name = MapGenerationGuards.Identity(name);
            TypeId = MapGenerationGuards.Identity(typeId);
            RuntimeType = runtimeType ?? throw new ArgumentNullException(nameof(runtimeType));
            if (runtimeType.ContainsGenericParameters || runtimeType == typeof(void) || runtimeType.IsByRef || runtimeType.IsPointer || runtimeType.IsByRefLike)
                throw new ArgumentException("A concrete data type is required.", nameof(runtimeType));
        }
    }

    public sealed class MapGenerationStepSpec
    {
        private readonly byte[] configuration;
        public string Key { get; }
        public string HandlerId { get; }
        public int ConfigurationVersion { get; }
        public int AlgorithmVersion { get; }
        public MapGenerationStage Stage { get; }
        public MapGenerationDomain Domain { get; }
        public ReadOnlyMemory<byte> Configuration => configuration.ToArray();
        public IReadOnlyList<string> Before { get; }
        public IReadOnlyList<string> After { get; }
        public IReadOnlyList<MapGenerationPort> Inputs { get; }
        public IReadOnlyList<MapGenerationPort> Outputs { get; }
        public int? NativeOrder { get; }
        public bool IsOpaque { get; }

        public MapGenerationStepSpec(string key, string handlerId, int configurationVersion, int algorithmVersion,
            MapGenerationStage stage, MapGenerationDomain domain, ReadOnlyMemory<byte> configuration = default,
            IEnumerable<string>? before = null, IEnumerable<string>? after = null,
            IEnumerable<MapGenerationPort>? inputs = null, IEnumerable<MapGenerationPort>? outputs = null,
            int? nativeOrder = null, bool isOpaque = false)
        {
            Key = MapGenerationGuards.Identity(key);
            HandlerId = MapGenerationGuards.Identity(handlerId);
            if (configurationVersion < 1 || algorithmVersion < 1) throw new ArgumentOutOfRangeException(nameof(configurationVersion));
            if (!Enum.IsDefined(stage) || !Enum.IsDefined(domain)) throw new ArgumentException("Unknown generation stage/domain.");
            ConfigurationVersion = configurationVersion;
            AlgorithmVersion = algorithmVersion;
            Stage = stage;
            Domain = domain;
            this.configuration = configuration.ToArray();
            Before = MapGenerationGuards.FreezeKeys(before);
            After = MapGenerationGuards.FreezeKeys(after);
            Inputs = MapGenerationGuards.FreezePorts(inputs);
            Outputs = MapGenerationGuards.FreezePorts(outputs);
            NativeOrder = nativeOrder;
            IsOpaque = isOpaque;
        }
    }

    public sealed class LocalMapRecipe
    {
        public string Id { get; }
        public IReadOnlyList<MapGenerationStepSpec> Steps { get; }
        public LocalMapRecipe(string id, IEnumerable<MapGenerationStepSpec> steps)
        {
            Id = MapGenerationGuards.Identity(id);
            ArgumentNullException.ThrowIfNull(steps);
            var copy = steps.ToArray();
            if (copy.Any(step => step == null)) throw new ArgumentException("Null recipe step.", nameof(steps));
            Steps = Array.AsReadOnly(copy);
        }
    }

    public sealed class LocalMapGenerationSpec
    {
        public LocalMapRecipe Recipe { get; }
        public IReadOnlyDictionary<MapGenerationPurpose, LocalMapRecipe> PurposeRecipes { get; }
        public string? NativeGeneratorId { get; }
        public LocalMapGenerationSpec(LocalMapRecipe recipe,
            IReadOnlyDictionary<MapGenerationPurpose, LocalMapRecipe>? purposeRecipes = null, string? nativeGeneratorId = null)
        {
            Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
            var copy = new Dictionary<MapGenerationPurpose, LocalMapRecipe>();
            foreach (var pair in purposeRecipes ?? new Dictionary<MapGenerationPurpose, LocalMapRecipe>())
            {
                if (!Enum.IsDefined(pair.Key) || pair.Value == null) throw new ArgumentException("Invalid purpose recipe.", nameof(purposeRecipes));
                copy.Add(pair.Key, pair.Value);
            }
            PurposeRecipes = new ReadOnlyDictionary<MapGenerationPurpose, LocalMapRecipe>(copy);
            NativeGeneratorId = nativeGeneratorId == null ? null : MapGenerationGuards.Identity(nativeGeneratorId);
        }
    }

    public interface IMapGenerationModule { void Execute(MapGenerationContext context); }
    public interface IMapGenerationPostInitializer { void PostInitialize(MapGenerationContext context); }

    internal static class MapGenerationGuards
    {
        internal static string Identity(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return value;
        }
        internal static IReadOnlyList<string> FreezeKeys(IEnumerable<string>? values)
        {
            var copy = (values ?? Array.Empty<string>()).Select(Identity).ToArray();
            if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("Duplicate ordering key.");
            return Array.AsReadOnly(copy);
        }
        internal static IReadOnlyList<MapGenerationPort> FreezePorts(IEnumerable<MapGenerationPort>? values)
        {
            var copy = (values ?? Array.Empty<MapGenerationPort>()).ToArray();
            if (copy.Any(port => port == null) || copy.Select(port => port.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length)
                throw new ArgumentException("Null or duplicate logical port.");
            return Array.AsReadOnly(copy);
        }
    }
}
