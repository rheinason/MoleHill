# Scatter

Distributes a **weighted mix of blocks** across closed regions or along open curves, placed on the terrain.
Use it for planting, rocks, fences, bollards and anything that repeats.

## Setting up

1. Add a Scatter card.
2. Choose the **Source** mode: **Within region** (fill the inside of closed boundaries) or **Along curve**.
3. Assign the **Boundaries** or **Curves**.
4. Under **Block Mix** press **Add** to pick block definitions from the document and give each a weight.
   A block with a higher weight is chosen more often.
5. Choose how many to scatter with a **Density Mode**.

## Settings

| Setting | Meaning |
|---|---|
| **Source** | **Within region**: fill closed boundaries. **Along curve**: distribute along open curves |
| **Boundaries / Curves** | The curves or layers for the scatter |
| **Block Mix** | The weighted list of blocks. Each block has a weight (relative likelihood) and a remove button |
| **Pattern** | *Region only.* **Random**, **Grid**, **Jittered Grid** or **Poisson** (blue-noise, evenly spaced but irregular) |
| **Density Mode** | **Total count**, **Per area**, **Min spacing** (region); **Total count**, **Centre spacing**, **Edge-to-edge** (curve) |
| **Count** | Total number of instances (Total count mode) |
| **Per Area** | Instances per square model unit (Per area mode) |
| **Spacing** | Minimum centre-to-centre distance (spacing modes) |
| **Edge Gap** | Gap left between block footprints (Edge-to-edge mode) |
| **Randomness** | *Curve only.* `0` is even; `1` allows up to half a spacing step of drift |
| **Block order** | *Curve with several blocks.* **Random (by weight)** or **In sequence** (cycle the list) |
| **XY Jitter** | *Curve only.* Random sideways offset radius for each placed point |
| **Align to tangent** | Turn instances to follow the curve direction |
| **Align to slope** | Tilt instances to the terrain normal |
| **Slope filter** | Only place instances where the terrain slope is between **Slope Min** (flattest) and **Slope Max** (steepest) |
| **Elevation filter** | Only place instances where the terrain height is between **Elevation Min** and **Elevation Max** |
| **Rotate Min/Max**, **Scale Min/Max**, **Seed**, **Z Offset** | Randomisation and offset, as on other [object cards](README.md#shared-settings) |
| **Preview** | How the scatter draws while you edit: **Point cloud**, **Shape points**, **Bounding boxes** or **Real (capped)**. Bake always produces real block instances |
| **Preview Cap** | Maximum items drawn in the live preview. `0` for no cap |

## Tips

- Use the **Seed** to try different arrangements of the same settings.
- Use the **Slope filter** to keep trees off steep batters and the **Elevation filter** to keep planting
  above a flood line.
- Keep the preview on **Point cloud** or **Bounding boxes** for large scatters; the preview cap protects
  responsiveness while you adjust.
- Scatter instances are created only on bake, so the document stays light while you design.

## Related

[Plant](plant.md) · [Orient](orient.md) · [Zones](../zones.md)
