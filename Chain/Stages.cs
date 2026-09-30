namespace Chaiser
{
    /// <summary>Walks a file's archives back to back (each header gives the next offset).</summary>
    public sealed class SplitContainers(IArchiveFormat format) : Stage<RawFile, Container>
    {
        public override IEnumerable<Container> Run(RawFile file, Worker worker)
        {
            // Sequential (and cheap): only headers and block tables are read. Collected first so
            // the file is closed before the containers are processed.
            var containers = new List<Container>();
            using var reader = new FileReader(file.Path);
            long position = 0;
            while (position + 8 <= reader.BaseStream.Length)
            {
                reader.Position = position;
                if (!format.IsHeader(reader)) break;
                try
                {
                    var archive = format.Open(reader, file.Path);
                    containers.Add(new Container(file, position, archive));
                    if (archive.NextContainerOffset <= position) break;
                    position = archive.NextContainerOffset;
                }
                catch (Exception e)
                {
                    // Keep the containers before it; the rest of the file can't be located.
                    worker.Fail(this, $"{file}@{position}", $"{e.GetType().Name}: {e.Message}");
                    break;
                }
            }
            return containers;
        }
    }

    /// <summary>Opens one archive at a known offset (entry from a map).</summary>
    public sealed class OpenContainer(IArchiveFormat format) : Stage<ContainerRef, Container>
    {
        public override IEnumerable<Container> Run(ContainerRef item, Worker worker)
        {
            BlockArchive archive;
            using (var reader = new FileReader(item.File.Path))
            {
                reader.Position = item.Offset;
                archive = format.Open(reader, item.File.Path);
            }
            yield return new Container(item.File, item.Offset, archive, item.Wanted);
        }
    }

    /// <summary>A container's serialized-file nodes (resource nodes are opened on demand instead).</summary>
    public sealed class OpenNodes : Stage<Container, NodeItem>
    {
        public static bool IsResource(string path) =>
            path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);

        public override IEnumerable<NodeItem> Run(Container container, Worker worker)
        {
            foreach (var node in container.Archive.Nodes)
            {
                if (container.Wanted is { IsEmpty: true }) yield break; // everything wanted was found
                if (IsResource(node.path)) continue;
                yield return new NodeItem(container, node);
            }
        }
    }

    /// <summary>Parses a node as a SerializedFile over its lazy stream; non-assets nodes yield nothing.</summary>
    public sealed class OpenAssetsFile(Func<int, bool> keepTypeTree = null) : Stage<NodeItem, AssetsFileItem>
    {
        public override IEnumerable<AssetsFileItem> Run(NodeItem node, Worker worker)
        {
            var file = Open(node, worker, keepTypeTree);
            if (file != null) yield return file;
        }

        public static AssetsFileItem Open(NodeItem node, Worker worker, Func<int, bool> keepTypeTree)
        {
            var container = node.Container;
            var reader = new FileReader(node.Node.path, container.Archive.OpenNodeLazy(node.Node, worker.Handle(container.File.Path)));
            try
            {
                if (reader.FileType != FileType.AssetsFile)
                {
                    reader.Dispose();
                    return null;
                }
                var file = new SerializedFile(reader, keepTypeTree) { Dialect = worker.Session.Dialect };
                return new AssetsFileItem(node, reader, file);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }
    }

    /// <summary>The wanted objects present in this file (each taken once), or all objects if nothing is wanted in particular.</summary>
    public sealed class SelectObjects : Stage<AssetsFileItem, ObjectItem>
    {
        public override IEnumerable<ObjectItem> Run(AssetsFileItem file, Worker worker)
        {
            var wanted = file.Container.Wanted;
            if (wanted == null)
            {
                foreach (var info in file.File.m_Objects) yield return new ObjectItem(file, info, null);
                yield break;
            }
            foreach (var pathId in wanted.Keys.Where(file.File.ObjectsByPathId.ContainsKey).ToList())
            {
                if (wanted.TryRemove(pathId, out var entry)) yield return new ObjectItem(file, file.File.ObjectsByPathId[pathId], entry);
            }
        }
    }

    /// <summary>Texture2D -> RGBA. An undecodable format is a Failure; so is anything that throws.</summary>
    public sealed class DecodeTexture : Stage<ObjectItem, TextureImage>
    {
        public override IEnumerable<TextureImage> Run(ObjectItem item, Worker worker)
        {
            var reader = item.Owner.Reader;
            reader.Position = 0;
            var texture = new Texture2D(new ObjectReader(reader, item.Owner.File, item.Info));
            var raw = ReadImageBytes(texture, item.Owner, worker);
            if (TextureExporter.TryDecodeToRgba(texture, raw, out var rgba))
                yield return new TextureImage(item.Entry, rgba, texture.m_Width, texture.m_Height);
            else
                worker.Fail(this, item, $"undecodable {texture.m_TextureFormat} {texture.m_Width}x{texture.m_Height} from {raw.Length} bytes");
        }

        static byte[] ReadImageBytes(Texture2D texture, AssetsFileItem owner, Worker worker)
        {
            var location = texture.image_data;
            if (location.IsInline)
            {
                owner.Reader.Position = location.Offset;
                return owner.Reader.ReadBytes((int)location.Size);
            }

            // "archive:/CAB-xxxx/CAB-xxxx.resS" -> sibling node "CAB-xxxx.resS" in this same container
            var nodeName = location.ExternalPath[(location.ExternalPath.LastIndexOf('/') + 1)..];
            var resource = worker.Resource(owner.Container, nodeName);
            var buffer = new byte[location.Size];
            resource.Position = location.Offset;
            resource.ReadExactly(buffer, 0, buffer.Length);
            return buffer;
        }
    }

    public sealed class WritePng(Func<AssetEntry, string> outPath) : Stage<TextureImage, Written>
    {
        public override IEnumerable<Written> Run(TextureImage image, Worker worker)
        {
            var path = outPath(image.Entry);
            TextureExporter.WritePng(image.Rgba, image.Width, image.Height, path);
            yield return new Written(image.Entry, path);
        }
    }
}
