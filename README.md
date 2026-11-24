# ThreeChartDemo - Blazor WebAssembly + NCalc + ThreeJS

Animated 3D surface chart of f(x, y, t), fully client side, hostable on any static web server.

* Visual Studio 2026 (or .NET 10 SDK): open `ThreeChartDemo.slnx`, press F5.
* Parser: NuGet `NCalc` (pure managed, works in WebAssembly), built-in functions are case-insensitive.
* ThreeJS 0.186.0 + OrbitControls from jsDelivr CDN via `importmap` in `wwwroot/index.html`. No npm, no Vite.
* C#: parsing, grid evaluation every frame, mesh + colours. JS (`wwwroot/js/scene.js`): rendering only.
* Animation: t runs from T min to T max and back, forever (ping-pong). Speed = time steps per second.

## Publish (static files)

    dotnet publish ThreeChartDemo/ThreeChartDemo.csproj -c Release -o publish

Upload the content of `publish/wwwroot` to any static host.
Sub-folder hosting: change `<base href="/" />` in `index.html`.

## AOT (recommended for animation)

    dotnet workload install wasm-tools

then uncomment the `RunAOTCompilation` block in the `.csproj`.

## Formula syntax

Variables `x`, `y`, `t`; constants `pi`, `e`; functions sin, cos, tan, sqrt, abs, exp, log, pow, ...
Power is `pow(x, 2)` (the `^` operator is not power in NCalc).

## Adaptive meshing

Tick "Adaptive mesh" in the UI. For every time step a separate mesh is built (before the animation starts):

1. uniform grid (X/Y divisions), each cell cut into two triangles (`Meshing/TriMesh.CreateGrid`),
2. f evaluated in the vertices,
3. each triangle: |f(centroid) - mean of vertex values| > epsilon  ->  put on the refine list,
4. each triangle on the list that is still flagged is refined by longest-edge bisection with
   longest-edge propagation (`TriMesh.Refine`); triangles divided as a side effect lose their flag,
5. steps 3-4 repeat at most "Max refinements" times (or until nothing is flagged / "Max triangles" is reached).

In the animation loop each precomputed mesh is sent whole to ThreeJS (`setMesh`).
