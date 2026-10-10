# Analysis

Analysis cards **evaluate** the terrain. The result is a measurement: a number, or a colour mapped onto the
mesh. Add them from the **Add Analysis** menu on the **Analysis** tab. (Cards that *draw* the terrain, such
as contours and labels, are [annotations](../annotations/README.md).)

| Card | Answers |
|---|---|
| [Earthworks](earthworks.md) | How much cut, fill and net volume? |
| [Slope](slope.md) | How steep is it? |
| [Aspect](aspect.md) | Which way does it face? |
| [Elevation](elevation.md) | How high is it? |
| [Cut / Fill](cut-fill.md) | Where did grading add or remove ground? |
| [Waterflow from Points](waterflow.md) | Where does water go from here? |
| [Catchments](catchments.md) | Which ground drains to which outlet? |
| [Ponding](ponding.md) | Where would water stand? |
| [Gradient Compliance](gradient-compliance.md) | Do landings and routes meet an accessibility standard? |

## How analyses behave

- **The eye on the Analysis tab** shows or hides analysis colours and analysis-owned outputs together.
- **One colour at a time.** Slope, Aspect, Elevation, Cut / Fill, Ponding, Catchments and Gradient Compliance
  colour the terrain. The terrain shows one colouring at a time, and a [Legend](../annotations/legend.md)
  keys whichever is showing.
- **Colour ramps** (Slope, Elevation, Cut / Fill, Ponding) can be dragged to change stops, the band interval
  or the mapped range; this recolours instantly without a rebuild. See
  [Concepts](../06-concepts.md#colour-ramps).
- **Results area.** Each card shows what the last build measured. *Rebuild required* means the figures have
  not been calculated yet.
- **Blocked cards explain themselves.** For example Earthworks says there is nothing to compare when no
  card has changed the ground and no reference has been given.
- **Colours on drawn outputs.** Cards that draw lines or markers have colour fields. *Clear* a colour to
  follow the layer it routes to.
- Figures appear in the [Report Table](../annotations/report-table.md) and the CSV export.
