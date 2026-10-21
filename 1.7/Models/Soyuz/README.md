# PFL Soyuz Prism Test Asset

This asset is a deliberately hard-surface, low-poly Soyuz-style test model for PFView.

## Requirement mapping

1. **Cylinders/cones become regular prisms/pyramids**
   - Core large stages: 16-sided regular prisms.
   - Upper core stage and payload fairing: 12-sided regular prisms/frusta.
   - Four strap-on boosters: 12-sided regular frusta.
   - Payload adapter, escape-tower shaft and all engine nozzles: 8-sided prisms/frusta.
   - Escape-tower tip: 8-sided regular pyramid.
2. **Regular prisms by default**
   - Every radial ring is an equal-angle regular N-gon; no elliptical or eccentric approximation.
3. **Thin surface detail is baked, not modelled**
   - Panel seams, rivets, shallow stiffeners and warning panels are represented by:
     - `1.7/Textures/PFView/Ships/Soyuz/SoyuzNormal.png`
     - `1.7/Textures/PFView/Ships/Soyuz/SoyuzAlbedo.png`
   - The low-poly asset does not contain separate sheet-metal strips, raised panel borders or small bevel geometry.
   - The normal map is baked from a procedural detail height field to tangent space.
4. **Unnecessary bevels removed**
   - The render mesh has no bevel modifier and no micro-bevel edge loops.
   - Facet edges remain sharp/flat-shaded; panel-edge highlights come from the normal map.

## Mesh statistics

- Vertices: 545
- Triangles: 1,014
- Material slots: 1
- UV layer: 1 (box projection, 24 m texture period; four panels per tile => 6 m panels on the surface)
- Native size: X = 10.2 m, Y = 10.2 m, Z = 50.51 m in final PFL axes
- Unit mesh: 1.0 x 1.0 x 1.0 around the origin
- Final axes: Y up, +Z nose/forward
- Source runtime mesh: `1.7/Models/Soyuz/PFL_Soyuz_Prism.obj`

## Runtime integration

- Part def: `PF_ShipPart_Soyuz`
- Runtime bounds used by `PartMatrix`: `(10.2, 10.2, 50.51)` metres
- Debug entry: Dev mode -> PFL test -> `PFView·联盟号棱柱测试资产`
- Shader: `PFLitNormal`
- The normal map uses a derivative-rebuilt tangent basis, so no tangent vertex attribute is required and the existing single-submesh batched attribute set remains sufficient.
- The current Soyuz demonstration takes a dedicated direct-draw path in `SoyuzModelAssets.TryDraw`; this avoids sending a different material through the normal `PF_ShipSkin` batch.

## Why 1,014 triangles?

The asset intentionally tests the prism/detail-bake rules rather than reproducing every CAD surface. At PFView distance, silhouette is carried by the core stages, four boosters, nozzles and escape tower; panel/rivet detail is carried by the normal map.

## Runtime notes (2026-10-21 branch)

- `PFLitNormal.vert.glsl` applies the inverse-transpose model normal matrix.
- `PFLitNormal.frag.glsl` decodes the normal map and reconstructs a tangent basis from screen-space derivatives of world position and UV. Runtime normal strength is 0.9; a restrained view-dependent specular/rim response makes the panel seams readable at PFView distance.
- The normal PNG is authored as raw data (Blender Non-Color). At runtime `SoyuzModelAssets` copies its pixels into a linear `Texture2D` before binding it to `_NormalTex`, preventing accidental sRGB gamma decoding.
- Preview: `D:\PJ\Blender-MCP-Test\PFL_Assets\Soyuz\Soyuz_Prism_Preview.png`
