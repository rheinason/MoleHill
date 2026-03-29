# Add Block-Attribute Terrain Annotation Analyses                                                                                     
                                                                                                                                        
  ## Summary                                                                                                                            
  Implement the three requested tools as new cards in the Rhino panel `Analysis` tab, using the existing analysis-output pipeline rather
  than the older marker UI.                                                                                                             
                                                                                                                                        
  The new cards will generate block instances, not `TextDot`s. Each placed annotation will populate named block attributes from sampled 
  terrain data. MoleHill will ship a default annotation block schema and allow each analysis card to override it with a user-selected   
  Rhino block definition.                                                                                                               
                                                                                                                                        
  New analysis types:                                                                                                                   
  - `CurveSlopeLabelAnalysisDefinition`                                                                                                 
  - `ProjectedElevationLabelAnalysisDefinition`                                                                                         
  - `PointSlopeLabelAnalysisDefinition`                                                                                                 
                                                                                                                                        
  These are output-generating analyses like contours, not terrain-preview analyses.                                                     
                                                                                                                                        
  ## Key Changes                                                                                                                        
  - Extend `AnalysisDefinition` polymorphism, add card types to the Analysis add menu, and wire new titles/icons/subtitles/collapsed    
  summaries.                                                                                                                            
  - Add shared annotation-output settings to the new analysis types:                                                                    
    - `Sources` (`SourceReferenceSet`)                                                                                                  
    - `OutputLayerPath`                                                                                                                 
    - `ColorArgb`                                                                                                                       
    - `BlockDefinitionName`                                                                                                             
    - `BlockScale`                                                                                                                      
    - `AttributePrefix`                                                                                                                 
    - `AttributeSuffix`                                                                                                                 
    - `ValueFormat`                                                                                                                     
  - Use named attribute tokens rather than free-form key/value editing in v1.                                                           
    - Supported tokens in the shipped default block schema:                                                                             
      - `VALUE`                                                                                                                         
      - `PREFIX`                                                                                                                        
      - `SUFFIX`                                                                                                                        
      - `UNIT`                                                                                                                          
      - `NAME`                                                                                                                          
      - `INDEX`                                                                                                                         
      - `DISTANCE`                                                                                                                      
    - When a custom Rhino block is used, MoleHill fills any matching attribute names and ignores missing ones.                          
  - Default block strategy:                                                                                                             
    - Ship one MoleHill annotation block family for analysis outputs.                                                                   
    - Allow per-analysis override to an existing Rhino block definition.                                                                
    - Default orientation is `World XY`.                                                                                                
    - No live rotation logic in v1; if users want rotated results, that is a post-bake/manual workflow for now.                         
                                                                                                                                        
  ### Tool-specific behavior                                                                                                            
                                                                                                                                        
  #### CurveSlopeLabelAnalysisDefinition                                                                                                
  - Curve sources only.                                                                                                                 
  - Fixed interval sampling by curve length.                                                                                            
  - Terrain-projected slope only.                                                                                                       
  - Include start/end and curve segment breakpoints in addition to interval samples.                                                    
  - Compute grade from projected spans using horizontal run.                                                                            
  - Place one block per span midpoint.                                                                                                  
  - Tokens:                                                                                                                             
    - `VALUE` = slope value                                                                                                             
    - `UNIT` = `%` or `deg`                                                                                                             
    - `DISTANCE` = distance along source curve to the label location                                                                    
    - `INDEX` = label sequence number                                                                                                   
                                                                                                                                        
  #### ProjectedElevationLabelAnalysisDefinition                                                                                        
  - Point and curve sources.                                                                                                            
  - Points: one projected annotation per point.                                                                                         
  - Curves: extract edit/control points from the curve’s editable representation, dedupe by model tolerance, then project to terrain.   
  - Tokens:                                                                                                                             
    - `VALUE` = projected elevation                                                                                                     
    - `UNIT` = document length unit label if available, otherwise blank                                                                 
    - `INDEX` = sequence number                                                                                                         
                                                                                                                                        
  #### PointSlopeLabelAnalysisDefinition                                                                                                
  - Point sources only.                                                                                                                 
  - Project points to terrain and sample local slope from mesh normal at closest mesh point.                                            
  - Tokens:                                                                                                                             
    - `VALUE` = local terrain slope
    - `UNIT` = `%` or `deg`                                                                                                             
    - `INDEX` = sequence number                                                                                                         
                                                                                                                                        
  ## Pipeline and Service Updates                                                                                                       
  - Extend `TerrainBuildService.BuildAnalyses(...)` to generate auxiliary `GeneratedRhinoObject` block instances for the three new      
  analysis types.                                                                                                                       
  - Extend `TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(...)` to include these new analyses so enable/disable state controls  
  viewport and bake visibility.                                                                                                         
  - Add a shared helper for analysis annotation block generation and attribute assignment.                                              
  - Use the contour-style output-layer fallback: empty `OutputLayerPath` falls back to the terrain auxiliary layer.                     
  - Leave the existing hidden marker subsystem untouched in this change.                                                                
                                                                                                                                        
  ## Public Interfaces / Types                                                                                                          
  - Add three new `AnalysisDefinition` subclasses.                                                                                      
  - Extend `TerrainAnalysisSummary` with generic annotation metrics:                                                                    
    - `GeneratedOutputCount`                                                                                                            
    - `SampleSourceCount`                                                                                                               
    - `SampleMin`                                                                                                                       
    - `SampleMax`                                                                                                                       
    - `SampleAverage`                                                                                                                   
  - Add serializer support and migration normalization for the new analysis types and their defaults.
                                                                                                                                        
  ## Test Plan                                                                                                                          
  - Unit tests for pure helpers:                                                                                                        
    - interval sample generation                                                                                                        
    - projected-span slope computation                                                                                                  
    - point-slope conversion percent/degrees                                                                                            
    - edit-point extraction and deduping                                                                                                
    - attribute token dictionary generation                                                                                             
  - Serialization round-trip tests for the three new analysis definitions and new summary fields.                                       
  - Manual Rhino validation:                                                                                                            
    - add objects and assign layers both work                                                                                           
    - blocks appear/disappear with analysis enable state                                                                                
    - default block attributes populate correctly                                                                                       
    - custom block definitions receive matching attributes                                                                              
    - output layer and color override behave correctly                                                                                  
    - curve slope labels update after terrain rebuilds                                                                                  
    - elevation labels land at projected edit points                                                                                    
    - point slope labels match percent/degree mode                                                                                      
  - Build verification should be done with Rhino closed; current baseline build is blocked by Rhino 8 locking plugin outputs.
                                                                                                                                        
  ## Assumptions                                                                                                                        
  - Attribute-driven blocks replace `TextDot` output for these new analyses.                                                            
  - Named tokens are sufficient for v1; no arbitrary per-card custom attribute map editor yet.                                          
  - Default annotation orientation is `World XY`; rotation controls are out of scope for v1.                                            
  - Support is limited to Rhino `Point` and `Curve` objects for these new analysis cards; no point-cloud flood mode in v1.              
  - These analyses generate outputs and summaries only; they do not become terrain color-preview modes.                                 