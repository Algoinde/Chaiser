"""
Renders a single mesh (FBX or glTF/.glb) as a top-down orthographic PNG sprite.

Usage (headless):
    blender --background --python render_sprite.py -- <model_path> <out_png> [resolution]

.glb files (Chaiser's output) embed their textures. FBX files (AnimeStudio's
FbxExporterContext output) expect sibling texture PNGs next to the FBX.
"""
import sys
import math
import bpy
from mathutils import Vector


def parse_args():
    argv = sys.argv
    usage = "Usage: blender --background --python render_sprite.py -- <model_path> <out_png> [resolution] [tile_size]"
    if "--" not in argv:
        raise SystemExit(usage)
    argv = argv[argv.index("--") + 1:]
    if len(argv) < 2:
        raise SystemExit(usage)
    model_path = argv[0]
    out_png = argv[1]
    resolution = int(argv[2]) if len(argv) > 2 else 1024
    # Optional: explicit world-unit tile size (e.g. FactoryBuildingTable.json's range.width,
    # in world units - 1 unit per grid tile). When given, framing uses this instead of the
    # mesh's own bounding box, since collision/connector geometry can extend slightly past
    # the platform's actual visual tile edge and made computed-bounds framing too loose.
    tile_size = float(argv[3]) if len(argv) > 3 else None
    return model_path, out_png, resolution, tile_size


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block_collection in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.cameras, bpy.data.lights):
        for block in list(block_collection):
            if block.users == 0:
                block_collection.remove(block)


def import_model(model_path):
    if model_path.lower().endswith((".glb", ".gltf")):
        bpy.ops.import_scene.gltf(filepath=model_path)
    else:
        bpy.ops.import_scene.fbx(filepath=model_path)
    return [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]


def world_bounds(mesh_objects):
    min_x = min_y = min_z = math.inf
    max_x = max_y = max_z = -math.inf
    for obj in mesh_objects:
        for corner in obj.bound_box:
            world_corner = obj.matrix_world @ Vector(corner)
            min_x = min(min_x, world_corner.x)
            min_y = min(min_y, world_corner.y)
            min_z = min(min_z, world_corner.z)
            max_x = max(max_x, world_corner.x)
            max_y = max(max_y, world_corner.y)
            max_z = max(max_z, world_corner.z)
    return (min_x, min_y, min_z), (max_x, max_y, max_z)


def setup_camera(min_corner, max_corner, padding_ratio=0.0, tile_size=None):
    min_x, min_y, min_z = min_corner
    max_x, max_y, max_z = max_corner
    top_z = max_z

    if tile_size is not None:
        # Frame tight to the known gameplay footprint, centered on the origin (both the body
        # and base platform are placed at world-space (0,0,0) by the extractor), rather than
        # trusting the mesh's own bounding box which can extend past the visual tile edge.
        center_x = 0.0
        center_y = 0.0
        ortho_scale = tile_size * (1.0 + padding_ratio)
    else:
        center_x = (min_x + max_x) / 2.0
        center_y = (min_y + max_y) / 2.0
        span_x = max_x - min_x
        span_y = max_y - min_y
        # Blender's Z is up in the FBX import's default orientation; frame the top-down view
        # across the horizontal (X/Y) extent, not depth, since that's what a factory-planner
        # icon needs (looking straight down the vertical axis at the building's footprint).
        ortho_scale = max(span_x, span_y, 0.01) * (1.0 + padding_ratio)

    camera_data = bpy.data.cameras.new("SpriteCamera")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = ortho_scale
    camera_data.clip_start = 0.01
    camera_data.clip_end = max(ortho_scale, max_z - min_z, 1.0) * 10.0

    camera_obj = bpy.data.objects.new("SpriteCamera", camera_data)
    bpy.context.scene.collection.objects.link(camera_obj)
    camera_obj.location = (center_x, center_y, top_z + ortho_scale)
    # Looking straight down -Z; the 180 degree Z rotation spins the view in-plane (matches the
    # planner's expected sprite orientation) without affecting framing/ortho_scale math above.
    camera_obj.rotation_euler = (0.0, 0.0, math.radians(180))

    bpy.context.scene.camera = camera_obj
    return camera_obj


def setup_lighting():
    # Flat/neutral lighting: a soft top-down key plus fill from the sides so there's no
    # harsh directional shadow, matching the existing hand-captured sprite style (soft rim
    # highlight, no strong cast shadow). Tune further once compared against real references.
    sun_data = bpy.data.lights.new("KeyLight", type="SUN")
    sun_data.energy = 2.0
    sun_data.angle = math.radians(45)
    sun_obj = bpy.data.objects.new("KeyLight", sun_data)
    bpy.context.scene.collection.objects.link(sun_obj)
    sun_obj.location = (0.0, 0.0, 10.0)
    sun_obj.rotation_euler = (math.radians(25), 0.0, math.radians(35))

    fill_data = bpy.data.lights.new("FillLight", type="SUN")
    fill_data.energy = 1.0
    fill_obj = bpy.data.objects.new("FillLight", fill_data)
    bpy.context.scene.collection.objects.link(fill_obj)
    fill_obj.location = (0.0, 0.0, 10.0)
    fill_obj.rotation_euler = (math.radians(-20), 0.0, math.radians(-145))

    world = bpy.context.scene.world
    if world is None:
        world = bpy.data.worlds.new("World")
        bpy.context.scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg is not None:
        bg.inputs[0].default_value = (1.0, 1.0, 1.0, 1.0)
        bg.inputs[1].default_value = 0.3  # low ambient fill, no strong world tint


def autocrop_to_alpha(png_path, padding_px=2):
    """
    Trims fully-transparent margins down to the visible content's bounding box (plus a small
    padding so edge antialiasing isn't clipped). Matches the existing hand-captured sprites'
    style, which are cropped tight to their content rather than rendered at a fixed canvas -
    the platform tile's true world-space bounds leave real background margin that a naive
    fixed-frame render doesn't remove on its own.
    """
    img = bpy.data.images.load(png_path)
    width, height = img.size
    pixels = list(img.pixels)  # flat RGBA floats, row 0 = bottom
    channels = img.channels

    min_x, min_y = width, height
    max_x, max_y = -1, -1
    for y in range(height):
        row_base = y * width * channels
        for x in range(width):
            alpha = pixels[row_base + x * channels + 3]
            if alpha > 0.0:
                if x < min_x: min_x = x
                if x > max_x: max_x = x
                if y < min_y: min_y = y
                if y > max_y: max_y = y

    bpy.data.images.remove(img)

    if max_x < min_x:
        return  # fully transparent render - nothing to crop, leave the file as-is

    min_x = max(0, min_x - padding_px)
    min_y = max(0, min_y - padding_px)
    max_x = min(width - 1, max_x + padding_px)
    max_y = min(height - 1, max_y + padding_px)
    crop_w = max_x - min_x + 1
    crop_h = max_y - min_y + 1

    # Re-load for cropping (pixel list above was read-only working data).
    img = bpy.data.images.load(png_path)
    src_pixels = list(img.pixels)
    cropped = [0.0] * (crop_w * crop_h * channels)
    for row in range(crop_h):
        src_y = min_y + row
        src_start = (src_y * width + min_x) * channels
        dst_start = row * crop_w * channels
        cropped[dst_start:dst_start + crop_w * channels] = src_pixels[src_start:src_start + crop_w * channels]

    out_img = bpy.data.images.new("cropped", width=crop_w, height=crop_h, alpha=True)
    out_img.pixels = cropped
    out_img.filepath_raw = png_path
    out_img.file_format = "PNG"
    out_img.save()
    bpy.data.images.remove(img)
    bpy.data.images.remove(out_img)


def setup_render(out_png, resolution):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 128
    scene.render.resolution_x = resolution
    scene.render.resolution_y = resolution
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.filepath = out_png

    # Blender 4.x defaults to the AgX view transform, which aggressively desaturates bright
    # colors as they approach/exceed 1.0 - exactly the emissive corner-light range this pipeline
    # relies on to look saturated (a barely-over-1.0 amber came out washed toward pale gray under
    # AgX). Standard applies no filmic/AgX tonemapping, so authored colors render as-is. This only
    # affects the final display/export encoding - the compositor graph below operates on
    # scene-linear data regardless, so it doesn't change whether the glare itself triggers.
    scene.view_settings.view_transform = "Standard"

    # Bloom glow on emissive corner lights, done natively in Cycles' compositor rather than as a
    # separate Python post-process (an earlier version blurred the flat rendered PNG in numpy,
    # which needed its own alignment bookkeeping against the Blender-side autocrop and was
    # generally a workaround for not having gotten the Glare node working). Two mistakes were
    # behind that failure, not a fundamental limitation of the node:
    #  1. Glaring the tonemapped Combined image instead of the raw Emit pass meant a plain
    #     brightness threshold picked up the white port-marker quads (brighter than the
    #     deliberately modest ~0.6x-strength corner lights) instead of the actual emissive pixels.
    #  2. quality='MEDIUM' (the default) computes the glare on an internally downsampled copy,
    #     which loses thin geometry like a 2-4px corner-bracket line before it ever reaches the
    #     threshold step - HIGH is required for small/thin emissive shapes to glow at all.
    # Isolating the Emit pass (pure, un-tonemapped emissive radiance, independent of every other
    # light source and material in the scene) and glaring THAT with threshold=0 (already-isolated
    # emission needs no re-thresholding) resolves both, then the blurred glow is added back onto
    # the beauty image additively so it doesn't touch anything non-emissive.
    scene.view_layers[0].use_pass_emit = True

    scene.use_nodes = True
    tree = scene.node_tree
    tree.nodes.clear()
    render_layers = tree.nodes.new("CompositorNodeRLayers")

    glare = tree.nodes.new("CompositorNodeGlare")
    glare.glare_type = "FOG_GLOW"
    glare.quality = "HIGH"
    glare.threshold = 0.0
    glare.size = 8
    glare.mix = 1.0  # fully-processed glare output; blended back onto Combined via Add below, not Glare's own mix

    add = tree.nodes.new("CompositorNodeMixRGB")
    add.blend_type = "ADD"
    add.inputs[0].default_value = 1.0

    composite = tree.nodes.new("CompositorNodeComposite")

    tree.links.new(render_layers.outputs["Emit"], glare.inputs["Image"])
    tree.links.new(render_layers.outputs["Image"], add.inputs[1])
    tree.links.new(glare.outputs["Image"], add.inputs[2])
    tree.links.new(add.outputs["Image"], composite.inputs["Image"])
    # Add (unlike Glare) drops alpha, which would make film_transparent's background opaque in
    # the final PNG - wire the original render's alpha through via Set Alpha.
    set_alpha = tree.nodes.new("CompositorNodeSetAlpha")
    tree.links.new(add.outputs["Image"], set_alpha.inputs["Image"])
    tree.links.new(render_layers.outputs["Alpha"], set_alpha.inputs["Alpha"])
    tree.links.new(set_alpha.outputs["Image"], composite.inputs["Image"])


def main():
    model_path, out_png, resolution, tile_size = parse_args()

    clear_scene()
    mesh_objects = import_model(model_path)
    if not mesh_objects:
        raise SystemExit(f"No mesh objects found after importing {model_path}")

    min_corner, max_corner = world_bounds(mesh_objects)
    setup_camera(min_corner, max_corner, tile_size=tile_size)
    setup_lighting()
    setup_render(out_png, resolution)

    bpy.ops.render.render(write_still=True)
    autocrop_to_alpha(out_png)


if __name__ == "__main__":
    main()
