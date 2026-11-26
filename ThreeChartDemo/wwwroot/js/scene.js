// Thin ThreeJS layer. All math and mesh generation is done in C#; this file only renders.
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";

const BOX = 10, HEIGHT = 5;
const FLOOR_GAP = 0.2;      // distance between the lowest possible surface point (y = 0) and the floor grid

let host, renderer, scene, camera, controls, resizeObserver;
let surface = null;
let wire = null;                 // line overlay (edges of the triangles), child of `surface`
let wireVisible = true;
let heat = null;                 // heat map on the floor (shares the surface geometry), child of `surface`
let heatVisible = true;
let labels = null;
let labelKey = "";

const wireMaterial = new THREE.LineBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.15 });

// ---- heat map material ----
// The heat map is the SAME mesh as the surface (same BufferGeometry object, so it follows every
// animation update for free), drawn with a custom shader:
//   vertex shader   : flattens the mesh onto the floor (y = const) and passes the original height
//                     (0..1) to the fragment shader as a varying;
//   fragment shader : quantises the interpolated height to 16 levels and looks the colour up in a
//                     uniform palette. No texture is needed: no filtering, mipmaps or edge bleeding
//                     to worry about, and the level borders are exactly sharp.
//                     Optionally (uniform uContours) it also draws thin anti-aliased lines on the
//                     borders between the levels, using screen-space derivatives (fwidth).
const HEAT_LEVELS = 16;
const HEAT_STOPS = [                      // same viridis-like stops as the C# colour map
    [0.267, 0.005, 0.329],
    [0.231, 0.322, 0.546],
    [0.128, 0.567, 0.551],
    [0.369, 0.789, 0.383],
    [0.993, 0.906, 0.144],
];

function paletteColor(t) {
    const x = Math.min(Math.max(t, 0), 1) * (HEAT_STOPS.length - 1);
    const i = Math.min(Math.floor(x), HEAT_STOPS.length - 2);
    const f = x - i;
    const a = HEAT_STOPS[i], b = HEAT_STOPS[i + 1];
    return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
}

const heatMaterial = new THREE.ShaderMaterial({
    side: THREE.DoubleSide,
    polygonOffset: true,                  // keep the floor grid lines visible on top of the colours
    polygonOffsetFactor: 1,
    polygonOffsetUnits: 1,
    uniforms: {
        // colour of level i = colour map sampled in the middle of the i-th of 16 equal blocks
        uPalette: {
            value: Array.from({ length: HEAT_LEVELS },
                (_, i) => new THREE.Vector3(...paletteColor((i + 0.5) / HEAT_LEVELS))),
        },
        uFloorY: { value: -FLOOR_GAP - 0.02 },   // just below the grid lines
        uHeight: { value: HEIGHT },
        uContours: { value: 1.0 },               // 1 = draw contour lines, 0 = off (set by setContours)
        uLineWidth: { value: 0.6 },              // contour line width in framebuffer pixels
    },
    vertexShader: /* glsl */ `
        uniform float uFloorY;
        uniform float uHeight;
        varying float vT;
        void main() {
            vT = position.y / uHeight;                         // f normalised to 0..1
            vec3 p = vec3(position.x, uFloorY, position.z);    // flatten onto the floor
            gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
        }
    `,
    fragmentShader: /* glsl */ `
        uniform vec3 uPalette[${HEAT_LEVELS}];
        uniform float uContours;
        uniform float uLineWidth;
        varying float vT;
        void main() {
            float x = clamp(vT, 0.0, 1.0) * ${HEAT_LEVELS}.0;          // 0 .. 16
            int level = int(min(floor(x), ${HEAT_LEVELS - 1}.0));
            vec3 color = uPalette[level];

            if (uContours > 0.5) {
                float n = floor(x + 0.5);                                // nearest level border
                if (n > 0.5 && n < ${HEAT_LEVELS - 1}.5) {               // skip the outer edges of the range
                    float w = max(fwidth(x), 1e-5);                      // how much x changes per pixel
                    float dist = abs(x - n) / w;                         // distance to the border in pixels
                    float line = 1.0 - smoothstep(0.5 * uLineWidth, 0.5 * uLineWidth + 1.0, dist);
                    color = mix(color, vec3(0.04), 0.25 * line);
                }
            }

            gl_FragColor = vec4(color, 1.0);
            #include <colorspace_fragment>
        }
    `,
});

export function init(hostElement) {
    host = hostElement;

    renderer = new THREE.WebGLRenderer({ antialias: true });
    host.appendChild(renderer.domElement);

    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0f141a);

    camera = new THREE.PerspectiveCamera(45, 1, 0.1, 300);

    controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;

    scene.add(new THREE.AmbientLight(0xffffff, 0.9));
    const sun = new THREE.DirectionalLight(0xffffff, 2.2);
    sun.position.set(8, 14, 10);
    scene.add(sun);
    const fill = new THREE.DirectionalLight(0x88aaff, 0.8);
    fill.position.set(-10, 6, -8);
    scene.add(fill);

    // Floor grid + bounding box, both lowered by FLOOR_GAP so the surface never touches the floor.
    const grid = new THREE.GridHelper(BOX, 10, 0x6b7a8a, 0x2f3b48);
    grid.position.y = -FLOOR_GAP;
    scene.add(grid);

    const boxHeight = HEIGHT + FLOOR_GAP;
    const box = new THREE.LineSegments(
        new THREE.EdgesGeometry(new THREE.BoxGeometry(BOX, boxHeight, BOX)),
        new THREE.LineBasicMaterial({ color: 0x55657a }));
    box.position.y = boxHeight / 2 - FLOOR_GAP;      // spans y = -FLOOR_GAP ... HEIGHT
    scene.add(box);

    // Keep the renderer in sync with the viewport: element size changes (window resize, panel
    // collapse, orientation change) and pixel-ratio changes (moving window between monitors, zoom).
    resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(host);
    window.addEventListener("resize", resize);

    resetView();
    resize();
    renderer.setAnimationLoop(render);
}

function render() {
    controls.update();
    renderer.render(scene, camera);
}

function resize() {
    if (!renderer) return;
    const rect = host.getBoundingClientRect();
    const w = Math.max(Math.floor(rect.width), 1);
    const h = Math.max(Math.floor(rect.height), 1);

    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    renderer.setPixelRatio(ratio);
    heatMaterial.uniforms.uLineWidth.value = 0.6 * ratio;     // same visual thickness on hi-dpi screens
    renderer.setSize(w, h, false);          // CSS size comes from the stylesheet (100% of host)
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    render();                               // repaint immediately, no blank flash while dragging
}

/** Resolves on the next browser animation frame; C# awaits this to drive the animation. */
export function nextFrame() {
    return new Promise(resolve => requestAnimationFrame(() => resolve()));
}

// Uint8Array (from a .NET byte[]) -> typed array view without copying when alignment allows.
function asFloat32(bytes) {
    return bytes.byteOffset % 4 === 0
        ? new Float32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4)
        : new Float32Array(bytes.slice().buffer);
}
function asUint32(bytes) {
    return bytes.byteOffset % 4 === 0
        ? new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 4)
        : new Uint32Array(bytes.slice().buffer);
}

// ---- wireframe overlay ----
// The lines SHARE the surface's `position` attribute, so when updateMesh() changes the vertex
// heights the lines move with the surface automatically. Only the edge index buffer is owned
// by the lines, and it is rebuilt only when the set of triangles changes.

/** Unique edges (a,b) of all triangles - like WireframeGeometry, but index buffer only. */
function buildEdges(triangleIndices) {
    const seen = new Set();
    const edges = [];
    const addEdge = (a, b) => {
        const lo = a < b ? a : b, hi = a < b ? b : a;
        const key = lo * 1048576 + hi;               // vertex count < 2^20
        if (!seen.has(key)) { seen.add(key); edges.push(lo, hi); }
    };
    for (let i = 0; i + 2 < triangleIndices.length; i += 3) {
        const a = triangleIndices[i], b = triangleIndices[i + 1], c = triangleIndices[i + 2];
        addEdge(a, b); addEdge(b, c); addEdge(c, a);
    }
    return new Uint32Array(edges);
}

function rebuildWireframe(triangleIndices) {
    disposeWire();

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", surface.geometry.getAttribute("position"));   // shared, not copied
    geometry.setIndex(new THREE.BufferAttribute(buildEdges(triangleIndices), 1));

    wire = new THREE.LineSegments(geometry, wireMaterial);
    wire.frustumCulled = false;
    wire.visible = wireVisible;
    surface.add(wire);
}

function disposeWire() {
    if (!wire) return;
    surface.remove(wire);
    wire.geometry.deleteAttribute("position");   // do not let dispose() free the surface's buffer
    wire.geometry.dispose();                     // frees only the edge index
    wire = null;
}

// ---- surface ----

/** Creates a new surface (new chart). */
export function setMesh(positionBytes, colorBytes, indexBytes, bounds) {
    if (surface) {
        disposeWire();
        heat = null;                    // child of the old surface; geometry and material are not owned by it
        scene.remove(surface);
        surface.geometry.dispose();
        surface.material.dispose();
    }

    const indices = asUint32(indexBytes);

    const geometry = new THREE.BufferGeometry();
    // Copy into JS-owned arrays: they are updated in place by updateMesh().
    geometry.setAttribute("position", new THREE.BufferAttribute(new Float32Array(asFloat32(positionBytes)), 3));
    geometry.setAttribute("color", new THREE.BufferAttribute(new Float32Array(asFloat32(colorBytes)), 3));
    geometry.setIndex(new THREE.BufferAttribute(new Uint32Array(indices), 1));
    geometry.setDrawRange(0, indices.length);
    geometry.computeVertexNormals();

    const material = new THREE.MeshStandardMaterial({
        vertexColors: true,
        side: THREE.DoubleSide,
        roughness: 0.55,
        metalness: 0.05,
        polygonOffset: true,                // push the filled triangles slightly back, so the
        polygonOffsetFactor: 1,             // lines drawn on top do not flicker (z-fighting)
        polygonOffsetUnits: 1,
    });

    surface = new THREE.Mesh(geometry, material);
    surface.frustumCulled = false;          // bounding sphere would go stale while animating
    scene.add(surface);

    // Heat map: same geometry object as the surface -> follows the animation without any extra work.
    heat = new THREE.Mesh(geometry, heatMaterial);
    heat.frustumCulled = false;
    heat.visible = heatVisible;
    surface.add(heat);

    rebuildWireframe(indices);

    buildLabels(bounds);
}

/** Animation frame: same vertex count, new heights/colours; indices only when they changed. */
export function updateMesh(positionBytes, colorBytes, indexBytes, bounds) {
    if (!surface) return;
    const geometry = surface.geometry;
    const position = geometry.getAttribute("position");
    const color = geometry.getAttribute("color");

    const p = asFloat32(positionBytes);
    if (p.length !== position.array.length) return;   // stale frame of an older chart

    position.array.set(p);
    position.needsUpdate = true;                       // surface AND wire lines (shared attribute)
    color.array.set(asFloat32(colorBytes));
    color.needsUpdate = true;

    if (indexBytes) {
        const idx = asUint32(indexBytes);
        const index = geometry.getIndex();
        if (idx.length > index.array.length) {
            // More triangles than the buffer can hold: rebuild the surface (and the lines).
            setMesh(positionBytes, colorBytes, indexBytes, bounds);
            return;
        }
        index.array.set(idx);
        index.array.fill(0, idx.length);               // degenerate tail, harmless for normals
        index.needsUpdate = true;
        geometry.setDrawRange(0, idx.length);

        rebuildWireframe(idx);                         // triangles changed -> new edge set
    }

    geometry.computeVertexNormals();
    buildLabels(bounds);
}

/** Shows / hides the line overlay on top of the filled triangles. */
export function setWireframe(value) {
    wireVisible = value;
    if (wire) wire.visible = value;
}

/** Shows / hides the 16-level heat map on the floor. */
export function setHeatMap(value) {
    heatVisible = value;
    if (heat) heat.visible = value;
}

/** Contour lines between the 16 levels of the heat map: the value is passed to the shader as a uniform. */
export function setContours(value) {
    heatMaterial.uniforms.uContours.value = value ? 1.0 : 0.0;
}

// ---- query string helpers (used for deep links like ?sample=Plane%20wave) ----

/** Value of the query string parameter, or null when it is not present. */
export function getQueryParam(name) {
    return new URLSearchParams(window.location.search).get(name);
}

/** Sets a query string parameter without reloading the page (and without adding a history entry). */
export function setQueryParam(name, value) {
    const params = new URLSearchParams(window.location.search);
    params.set(name, value);
    // URLSearchParams writes spaces as "+"; a literal "+" is already escaped as %2B, so this is safe.
    const query = params.toString().replace(/\+/g, "%20");
    window.history.replaceState(window.history.state, "", window.location.pathname + "?" + query + window.location.hash);
}

export function resetView() {
    camera.position.set(11, 9, 13);
    controls.target.set(0, 2, 0);
    controls.update();
}

export function dispose() {
    renderer?.setAnimationLoop(null);
    resizeObserver?.disconnect();
    window.removeEventListener("resize", resize);
    controls?.dispose();
    disposeWire();
    if (surface) { surface.geometry.dispose(); surface.material.dispose(); }
    wireMaterial.dispose();
    heatMaterial.dispose();
    renderer?.dispose();
    renderer?.domElement.remove();
    renderer = null;
}

// ---- axis labels (sprites with canvas text) ----

function buildLabels(bounds) {
    const key = bounds.map(fmt).join("|");
    if (key === labelKey) return;           // nothing changed - keep the existing sprites
    labelKey = key;

    const [xMin, xMax, yMin, yMax, fMin, fMax] = bounds;

    if (labels) {
        scene.remove(labels);
        labels.traverse(o => { o.material?.map?.dispose(); o.material?.dispose(); });
    }
    labels = new THREE.Group();

    const h = BOX / 2;
    // math x -> X axis, math y -> -Z axis, f -> Y axis
    add(fmt(xMin), -h, -FLOOR_GAP, h + 0.9);
    add(fmt(xMax), h, -FLOOR_GAP, h + 0.9);
    add("x", 0, -FLOOR_GAP, h + 1.8, "#7fb4ff");

    add(fmt(yMin), h + 0.9, -FLOOR_GAP, h);
    add(fmt(yMax), h + 0.9, -FLOOR_GAP, -h);
    add("y", h + 1.8, -FLOOR_GAP, 0, "#7fb4ff");

    add(fmt(fMin), -h - 0.9, 0, h);
    add(fmt(fMax), -h - 0.9, HEIGHT, h);
    add("f", -h - 0.9, HEIGHT / 2, h, "#7fb4ff");

    scene.add(labels);
}

function add(text, x, y, z, color = "#c9d4df") {
    const canvas = document.createElement("canvas");
    canvas.width = 256; canvas.height = 64;
    const ctx = canvas.getContext("2d");
    ctx.font = "bold 34px Consolas, monospace";
    ctx.textAlign = "center"; ctx.textBaseline = "middle";
    ctx.fillStyle = color;
    ctx.fillText(text, 128, 32);

    const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
        map: new THREE.CanvasTexture(canvas), depthTest: false, transparent: true,
    }));
    sprite.scale.set(2.4, 0.6, 1);
    sprite.position.set(x, y, z);
    labels.add(sprite);
}

function fmt(v) {
    return String(Number(v.toPrecision(3)));
}
