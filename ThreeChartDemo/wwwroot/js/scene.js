// Thin ThreeJS layer. All math and mesh generation is done in C#; this file only renders.
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";

const BOX = 10, HEIGHT = 5;
const FLOOR_GAP = 0.1;      // distance between the lowest possible surface point (y = 0) and the floor grid

let host, renderer, scene, camera, controls, resizeObserver;
let surface = null;
let wire = null;                 // line overlay (edges of the triangles), child of `surface`
let wireVisible = true;
let labels = null;
let labelKey = "";

const wireMaterial = new THREE.LineBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.15 });

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

    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
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
