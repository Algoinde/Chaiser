using System.Text;
using Newtonsoft.Json;

namespace Chaiser
{
    // One material slot's worth of a part: a contiguous slice of Indices (a flat triangle-index
    // list, in Unity winding/coordinate space - GltfExporter negates Z and reverses winding when
    // writing) plus the material/textures that slice uses. EmissivePng is separate from
    // BaseColorPng (not blended into it) - e.g. pedestal corner lights are a genuinely distinct
    // _EmissiveMap texture (T_fac_build_pedestal+1_003_E) layered on top of the base color via
    // the glTF material's own emissive channel, matching how the game's shader keeps them as
    // separate texture slots (_BaseColorMap, _EmissiveMap) rather than one baked image.
    public record GltfMaterialSlot(int IndexStart, int IndexCount, byte[] BaseColorPng, byte[] EmissivePng, string MaterialName,
        byte[] NormalPng = null, byte[] OrmPng = null);

    // One resolved material slot before index offsets are laid out: textures already in glTF's
    // layout (see BuildingExtractor.ResolveSurfacePngs).
    public record MaterialSlotData(int IndexCount, byte[] BaseColorPng, byte[] EmissivePng, byte[] NormalPng, byte[] OrmPng, string Name);

    // Raw geometry for one glTF mesh/node, decoupled from AnimeStudio's Mesh class so synthetic
    // geometry (e.g. a flat port-marker quad, which has no backing game asset at all) can be
    // exported through the exact same path as real extracted meshes - GltfExporter only ever
    // needs plain arrays, never Mesh's ObjectReader-bound internals. A part can have more than
    // one material slot (Unity submeshes) - e.g. a storage tank where slot 0 is an atlas-only
    // shader with no static texture, slot 1 is the real tank housing texture, and slot 2 is the
    // liquid window. MaterialSlots is empty for geometry with no resolvable material at all - the
    // exporter falls back to exporting it untextured in that case.
    public record GltfPart(
        string Name,
        int VertexCount,
        float[] Positions,
        float[] Normals,
        float[] UV0,
        IReadOnlyList<uint> Indices,
        IReadOnlyList<GltfMaterialSlot> MaterialSlots)
    {
        // Unity submeshes map 1:1 to material slots by index (m_materials[i] renders
        // m_SubMeshes[i]'s index range). Mesh.GetTriangles concatenates every submesh into one
        // flat m_Indices list in submesh order and rewrites each SubMesh.indexCount to the final
        // post-destripify/quad-conversion triangle-index count, so a running offset over
        // mesh.m_SubMeshes gives an exact, already-triangulated slice per slot - no separate
        // byte-offset math needed.
        public static GltfPart FromMesh(Mesh mesh, IReadOnlyList<MaterialSlotData> resolvedMaterials)
        {
            var materialSlots = new List<GltfMaterialSlot>();
            var indexOffset = 0;
            foreach (var m in resolvedMaterials)
            {
                materialSlots.Add(new GltfMaterialSlot(indexOffset, m.IndexCount, m.BaseColorPng, m.EmissivePng, m.Name, m.NormalPng, m.OrmPng));
                indexOffset += m.IndexCount;
            }

            return new GltfPart(mesh.Name, mesh.m_VertexCount, mesh.m_Vertices, mesh.m_Normals, mesh.m_UV0, mesh.m_Indices, materialSlots);
        }

        // A flat, single-sided quad on the XZ plane (Unity space: X-right, Z-forward, Y-up),
        // facing +Y (up) - i.e. lying flat on the ground, visible from directly above -
        // positioned at (centerX, y, centerZ) and yawed yawDegrees around the Y axis before
        // translation. sizeX/sizeZ are full width/depth in local (pre-rotation) world units.
        // Used for synthetic port-direction markers: no game mesh backs these, they're drawn
        // purely from the port position/rotation data in FactoryBuildingTable.json - each port's
        // trans.rotation.y (Unity's standard Y-axis-only yaw for a ground-facing marker) is
        // passed straight through as yawDegrees.
        public static GltfPart Quad(string name, float sizeX, float sizeZ, float centerX, float y, float centerZ, float yawDegrees, byte[] baseColorPng, string materialName)
        {
            var hx = sizeX / 2f;
            var hz = sizeZ / 2f;
            // Unity rotates clockwise-positive around +Y when viewed from above (left-handed
            // convention); this matches how Unity's Euler Y rotation is authored in the port
            // transform data, so no sign flip is needed here despite the later Unity->glTF
            // handedness conversion in ExportGlb (that conversion only touches Z, not rotation
            // composition within this still-Unity-space local quad).
            var rad = yawDegrees * MathF.PI / 180f;
            var cos = MathF.Cos(rad);
            var sin = MathF.Sin(rad);

            (float x, float z) Rotate(float lx, float lz) => (lx * cos + lz * sin, -lx * sin + lz * cos);

            var (x0, z0) = Rotate(-hx, -hz);
            var (x1, z1) = Rotate(hx, -hz);
            var (x2, z2) = Rotate(hx, hz);
            var (x3, z3) = Rotate(-hx, hz);

            // Unity UV origin is bottom-left; GltfExporter flips V on write to match glTF's
            // top-left origin, so author UVs here in Unity's convention like every other mesh.
            var positions = new[]
            {
                centerX + x0, y, centerZ + z0,
                centerX + x1, y, centerZ + z1,
                centerX + x2, y, centerZ + z2,
                centerX + x3, y, centerZ + z3,
            };
            var normals = new[]
            {
                0f, 1f, 0f,
                0f, 1f, 0f,
                0f, 1f, 0f,
                0f, 1f, 0f,
            };
            var uv0 = new[]
            {
                0f, 0f,
                1f, 0f,
                1f, 1f,
                0f, 1f,
            };
            // Authored so that after ExportGlb's universal Unity->glTF winding swap (a,b,c ->
            // a,c,b, applied to every part to compensate for the Z-axis mirror on positions),
            // the final winding is CCW as seen from +Y - matching the (0,1,0) normal above.
            // Confirmed by measurement: with 0,1,2,0,2,3 authored here, the exported (post-swap)
            // triangle's geometric face normal came out as (0,-1,0), opposite the vertex normal
            // - Cycles then shaded the quad as unlit/black despite everything else (UVs, alpha,
            // embedded texture bytes) being correct.
            var indices = new uint[] { 0, 2, 1, 0, 3, 2 };
            var materialSlots = new[] { new GltfMaterialSlot(0, indices.Length, baseColorPng, null, materialName) };
            return new GltfPart(name, 4, positions, normals, uv0, indices, materialSlots);
        }

        // Debug helper: an untextured axis-aligned box centered at (centerX, centerY, centerZ).
        // Used to sanity-check that synthetic-geometry placement/transform math is correct,
        // independent of texture/material/alpha-blend concerns (which Quad's textured markers
        // also depend on) - a plain opaque box is either visibly there or it isn't.
        public static GltfPart Cube(string name, float size, float centerX, float centerY, float centerZ, string materialName)
        {
            var h = size / 2f;
            var positions = new List<float>();
            var normals = new List<float>();
            var indices = new List<uint>();

            void Face(float[] n, float[][] corners)
            {
                var baseIdx = (uint)(positions.Count / 3);
                foreach (var c in corners)
                {
                    positions.Add(centerX + c[0]); positions.Add(centerY + c[1]); positions.Add(centerZ + c[2]);
                    normals.Add(n[0]); normals.Add(n[1]); normals.Add(n[2]);
                }
                indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
                indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
            }

            Face(new[] { 0f, 1f, 0f }, new[] { new[] { -h, h, -h }, new[] { h, h, -h }, new[] { h, h, h }, new[] { -h, h, h } }); // top
            Face(new[] { 0f, -1f, 0f }, new[] { new[] { -h, -h, h }, new[] { h, -h, h }, new[] { h, -h, -h }, new[] { -h, -h, -h } }); // bottom
            Face(new[] { 0f, 0f, 1f }, new[] { new[] { -h, -h, h }, new[] { -h, h, h }, new[] { h, h, h }, new[] { h, -h, h } }); // +Z
            Face(new[] { 0f, 0f, -1f }, new[] { new[] { h, -h, -h }, new[] { h, h, -h }, new[] { -h, h, -h }, new[] { -h, -h, -h } }); // -Z
            Face(new[] { 1f, 0f, 0f }, new[] { new[] { h, -h, h }, new[] { h, h, h }, new[] { h, h, -h }, new[] { h, -h, -h } }); // +X
            Face(new[] { -1f, 0f, 0f }, new[] { new[] { -h, -h, -h }, new[] { -h, h, -h }, new[] { -h, h, h }, new[] { -h, -h, h } }); // -X

            var materialSlots = new[] { new GltfMaterialSlot(0, indices.Count, null, null, materialName) };
            return new GltfPart(name, positions.Count / 3, positions.ToArray(), normals.ToArray(), null, indices, materialSlots);
        }
    }

    /// <summary>
    /// Minimal glTF 2.0 (.glb, binary) writer covering exactly what a static building sprite
    /// needs: one or more mesh/primitive parts (e.g. building body + base pedestal, which the
    /// game itself composites at the same identity transform - confirmed empirically, both
    /// storager_1 and pedestal+1_001_03x03_01 have localTransform position (0,0,0) with no
    /// rotation/scale, so no relative offsetting is needed), each with its own base color
    /// texture per material slot. Written by hand instead of pulling in a full glTF SDK or the
    /// native FBX wrapper AnimeStudio uses, since the format itself is simple enough (a JSON
    /// chunk + a binary buffer chunk, both length-prefixed) that a dependency isn't worth it.
    /// </summary>
    public static class GltfExporter
    {
        public static void ExportGlb(string outPath, IReadOnlyList<GltfPart> parts)
        {
            if (parts.Count == 0)
            {
                throw new InvalidOperationException("No parts to export.");
            }

            using var bin = new MemoryStream();
            using var bw = new BinaryWriter(bin);

            void Pad4()
            {
                while (bin.Length % 4 != 0) bw.Write((byte)0);
            }

            var bufferViews = new List<object>();
            var accessors = new List<object>();
            var meshes = new List<object>();
            var nodes = new List<object>();
            var materials = new List<object>();
            var textures = new List<object>();
            var images = new List<object>();
            var usedEmissiveStrengthExtension = false;

            foreach (var part in parts)
            {
                if (part.Positions == null || part.Indices.Count == 0)
                {
                    continue; // e.g. a collision-only mesh with no render geometry - skip, don't fail the whole export.
                }

                var vertexCount = part.VertexCount;
                var positions = part.Positions;
                var normals = part.Normals;
                var uv0Raw = part.UV0;

                // Unity is left-handed (X-right, Y-up, Z-forward); glTF is right-handed
                // (X-right, Y-up, Z-backward). Negating Z converts between them correctly -
                // negating X instead would also "fix" a naive left/right flip but is the wrong
                // axis and produces different (still incorrect) results on rotated geometry.
                // Triangle winding is reversed below to compensate so faces still point out.
                var posByteOffset = (int)bin.Length;
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (var i = 0; i < vertexCount; i++)
                {
                    var x = positions[i * 3];
                    var y = positions[i * 3 + 1];
                    var z = -positions[i * 3 + 2];
                    bw.Write(x); bw.Write(y); bw.Write(z);
                    if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                    if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
                }
                Pad4();

                var posBufferViewIdx = bufferViews.Count;
                bufferViews.Add(new { buffer = 0, byteOffset = posByteOffset, byteLength = vertexCount * 12, target = 34962 });
                var posAccessorIdx = accessors.Count;
                accessors.Add(new
                {
                    bufferView = posBufferViewIdx,
                    componentType = 5126,
                    count = vertexCount,
                    type = "VEC3",
                    min = new[] { minX, minY, minZ },
                    max = new[] { maxX, maxY, maxZ }
                });

                var attributes = new Dictionary<string, int> { ["POSITION"] = posAccessorIdx };

                if (normals != null && normals.Length >= vertexCount * 3)
                {
                    var normalByteOffset = (int)bin.Length;
                    for (var i = 0; i < vertexCount; i++)
                    {
                        bw.Write(normals[i * 3]);
                        bw.Write(normals[i * 3 + 1]);
                        bw.Write(-normals[i * 3 + 2]);
                    }
                    Pad4();

                    var normalBufferViewIdx = bufferViews.Count;
                    bufferViews.Add(new { buffer = 0, byteOffset = normalByteOffset, byteLength = vertexCount * 12, target = 34962 });
                    attributes["NORMAL"] = accessors.Count;
                    accessors.Add(new { bufferView = normalBufferViewIdx, componentType = 5126, count = vertexCount, type = "VEC3" });
                }

                if (uv0Raw != null)
                {
                    var uvStride = uv0Raw.Length / vertexCount; // 2 or 4 observed on Endfield meshes
                    var uvByteOffset = (int)bin.Length;
                    for (var i = 0; i < vertexCount; i++)
                    {
                        var u = uv0Raw[i * uvStride];
                        var v = uv0Raw[i * uvStride + 1];
                        bw.Write(u);
                        bw.Write(1f - v); // Unity UV origin is bottom-left; glTF/most viewers expect top-left.
                    }
                    Pad4();

                    var uvBufferViewIdx = bufferViews.Count;
                    bufferViews.Add(new { buffer = 0, byteOffset = uvByteOffset, byteLength = vertexCount * 8, target = 34962 });
                    attributes["TEXCOORD_0"] = accessors.Count;
                    accessors.Add(new { bufferView = uvBufferViewIdx, componentType = 5126, count = vertexCount, type = "VEC2" });
                }

                var maxIndex = part.Indices.Count == 0 ? 0u : part.Indices.Max();
                var useShort = maxIndex <= ushort.MaxValue;

                // One glTF primitive per material slot, all sharing the attribute accessors
                // above - each slot's indices are a contiguous slice of the part's flat index
                // list. A part with no resolvable material slots at all still gets exported
                // whole, as a single untextured primitive, so geometry is never silently dropped.
                var slots = part.MaterialSlots.Count > 0
                    ? part.MaterialSlots
                    : new[] { new GltfMaterialSlot(0, part.Indices.Count, null, null, "Material") };

                var primitives = new List<object>();
                foreach (var slot in slots)
                {
                    if (slot.IndexCount <= 0) continue;

                    var indexByteOffset = (int)bin.Length;
                    // Swap the last two indices of each triangle to reverse winding order,
                    // compensating for the Z-axis mirror above (otherwise every face points the
                    // wrong way and gets back-face culled - the geometry would render as hollow).
                    for (var i = slot.IndexStart; i + 3 <= slot.IndexStart + slot.IndexCount; i += 3)
                    {
                        var a = part.Indices[i];
                        var b = part.Indices[i + 1];
                        var c = part.Indices[i + 2];
                        if (useShort)
                        {
                            bw.Write((ushort)a); bw.Write((ushort)c); bw.Write((ushort)b);
                        }
                        else
                        {
                            bw.Write(a); bw.Write(c); bw.Write(b);
                        }
                    }
                    var indexByteLength = (int)bin.Length - indexByteOffset;
                    Pad4();

                    var indexBufferViewIdx = bufferViews.Count;
                    bufferViews.Add(new { buffer = 0, byteOffset = indexByteOffset, byteLength = indexByteLength, target = 34963 });
                    var indexAccessorIdx = accessors.Count;
                    accessors.Add(new { bufferView = indexBufferViewIdx, componentType = useShort ? 5123 : 5125, count = slot.IndexCount, type = "SCALAR" });

                    int WriteImageAndGetTextureIndex(byte[] png)
                    {
                        var imageByteOffset = (int)bin.Length;
                        var imageByteLength = png.Length;
                        bw.Write(png);
                        Pad4();

                        var imageBufferViewIdx = bufferViews.Count;
                        bufferViews.Add(new { buffer = 0, byteOffset = imageByteOffset, byteLength = imageByteLength });
                        var imageIdx = images.Count;
                        images.Add(new { bufferView = imageBufferViewIdx, mimeType = "image/png" });
                        var textureIdx = textures.Count;
                        textures.Add(new { source = imageIdx });
                        return textureIdx;
                    }

                    int materialIdx;
                    if (slot.BaseColorPng != null)
                    {
                        var baseColorTextureIdx = WriteImageAndGetTextureIndex(slot.BaseColorPng);

                        // Emissive is a separate texture slot layered additively on top of the
                        // base color by the glTF material's own emissive channel - matching the
                        // game shader's _EmissiveMap, kept distinct from _BaseColorMap rather
                        // than baked together (e.g. pedestal corner lights: the housing surface
                        // uses base color, only the small corner-bracket shapes are emissive).
                        object emissiveTexture = null;
                        float[] emissiveFactor = null;
                        if (slot.EmissivePng != null)
                        {
                            var emissiveTextureIdx = WriteImageAndGetTextureIndex(slot.EmissivePng);
                            emissiveTexture = new { index = emissiveTextureIdx };
                            // The _EmissiveMap texture itself is a flat white mask (confirmed:
                            // brightest pixel is (255,255,255)) - the actual in-game glow color
                            // comes from sampling an _EmissiveLut gradient texture at a
                            // per-material index, which we don't decode. Approximated instead
                            // with a saturated amber tint (matched against an in-game reference
                            // screenshot) multiplied onto the mask via emissiveFactor.
                            // render_sprite.py's compositor glares the isolated Emit pass (raw
                            // scene-linear radiance, independent of the beauty image's albedo
                            // and tonemapping) rather than this material's rendered color
                            // directly, so emissiveStrength here only needs to give the Emit
                            // pass real HDR headroom to glow from - it doesn't need to visually
                            // clip in the beauty render the way an earlier attempt assumed.
                            emissiveFactor = new[] { 255f / 255f, 213f / 255f, 25f / 255f };
                            usedEmissiveStrengthExtension = true;
                        }

                        // Surface maps: one ORM texture serves both metallic-roughness and
                        // occlusion, as glTF allows; the factors then pass the texture through.
                        var normalTexture = slot.NormalPng != null ? new { index = WriteImageAndGetTextureIndex(slot.NormalPng) } : null;
                        var ormTextureIdx = slot.OrmPng != null ? WriteImageAndGetTextureIndex(slot.OrmPng) : -1;

                        materialIdx = materials.Count;
                        materials.Add(new
                        {
                            name = slot.MaterialName,
                            pbrMetallicRoughness = new
                            {
                                baseColorTexture = new { index = baseColorTextureIdx },
                                metallicRoughnessTexture = ormTextureIdx >= 0 ? new { index = ormTextureIdx } : null,
                                metallicFactor = ormTextureIdx >= 0 ? 1.0 : 0.0,
                                roughnessFactor = 1.0,
                            },
                            normalTexture,
                            occlusionTexture = ormTextureIdx >= 0 ? new { index = ormTextureIdx } : null,
                            emissiveTexture,
                            emissiveFactor,
                            extensions = emissiveTexture != null
                                ? new Dictionary<string, object> { ["KHR_materials_emissive_strength"] = new { emissiveStrength = 3.0 } }
                                : null,
                            alphaMode = "MASK",
                            alphaCutoff = 0.5,
                            doubleSided = true,
                        });
                    }
                    else
                    {
                        materialIdx = materials.Count;
                        materials.Add(new { name = slot.MaterialName });
                    }

                    primitives.Add(new { attributes, indices = indexAccessorIdx, material = materialIdx });
                }

                if (primitives.Count == 0)
                {
                    continue; // every slot was empty - nothing to draw for this part.
                }

                var meshIdx = meshes.Count;
                meshes.Add(new { name = part.Name, primitives });

                nodes.Add(new { mesh = meshIdx, name = part.Name });
            }

            if (meshes.Count == 0)
            {
                throw new InvalidOperationException("No parts had exportable render geometry.");
            }

            var binBytes = bin.ToArray();

            var gltf = new
            {
                asset = new { version = "2.0", generator = "Chaiser" },
                scene = 0,
                scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
                extensionsUsed = usedEmissiveStrengthExtension ? new[] { "KHR_materials_emissive_strength" } : null,
                nodes,
                meshes,
                materials,
                textures,
                images,
                buffers = new[] { new { byteLength = binBytes.Length } },
                bufferViews,
                accessors,
            };

            var json = JsonConvert.SerializeObject(gltf, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var jsonPadded = PadTo4(jsonBytes, (byte)' ');
            var binPadded = PadTo4(binBytes, 0);

            using var fs = File.Create(outPath);
            using var writer = new BinaryWriter(fs);

            const uint GLB_MAGIC = 0x46546C67; // "glTF"
            const uint JSON_CHUNK_TYPE = 0x4E4F534A; // "JSON"
            const uint BIN_CHUNK_TYPE = 0x004E4942; // "BIN\0"

            var totalLength = 12 + 8 + jsonPadded.Length + 8 + binPadded.Length;

            writer.Write(GLB_MAGIC);
            writer.Write((uint)2);
            writer.Write((uint)totalLength);

            writer.Write((uint)jsonPadded.Length);
            writer.Write(JSON_CHUNK_TYPE);
            writer.Write(jsonPadded);

            writer.Write((uint)binPadded.Length);
            writer.Write(BIN_CHUNK_TYPE);
            writer.Write(binPadded);
        }

        private static int PadAmount(int length) => (4 - length % 4) % 4;

        private static byte[] PadTo4(byte[] data, byte padByte)
        {
            var pad = PadAmount(data.Length);
            if (pad == 0) return data;
            var result = new byte[data.Length + pad];
            Array.Copy(data, result, data.Length);
            for (var i = data.Length; i < result.Length; i++) result[i] = padByte;
            return result;
        }
    }
}
