// usd2glb — a USD stage's meshes as one binary glTF (ADR-283 §5.2).
//
//   usd2glb <in.usd|usda|usdc|usdz> <out.glb>
//
// Reads the stage with tinyusdz (Apache-2.0), lets its Tydra converter triangulate each mesh and compute each node's
// world matrix, and writes one primitive per placed mesh: positions in metres with Y up, as glTF has them, indices,
// and one material per primitive whose colour is the bound UsdPreviewSurface's diffuse colour or else the mesh's
// displayColor. No textures, skins, animation or normals: a 3D object layer stores the shape and a colour, and the
// server's GltfTriangles reads nothing more. Run by the server in a process of its own, under the same limits as
// Assimp (ADR-282), so a hostile file ends one conversion and not the server.
//
// Exit 0 with the file written; 1 when the stage cannot be read or holds no triangles; 2 on the wrong arguments.

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "io-util.hh"
#include "tinyusdz.hh"
#include "tydra/render-data.hh"

namespace {

struct Primitive {
  std::vector<float> positions;  // x, y, z
  std::vector<uint32_t> indices;
  float min[3]{INFINITY, INFINITY, INFINITY};
  float max[3]{-INFINITY, -INFINITY, -INFINITY};
  float color[3]{0.18f, 0.18f, 0.18f};
};

struct Frame {
  bool zUp{false};
  double metres{1.0};
};

void Place(const tinyusdz::tydra::RenderScene &scene, const tinyusdz::tydra::Node &node, const Frame &frame,
           std::vector<Primitive> *out) {
  if (node.nodeType == tinyusdz::tydra::NodeType::Mesh && node.id >= 0 &&
      static_cast<size_t>(node.id) < scene.meshes.size()) {
    const tinyusdz::tydra::RenderMesh &mesh = scene.meshes[static_cast<size_t>(node.id)];
    const std::vector<uint32_t> &counts = mesh.faceVertexCounts();
    const std::vector<uint32_t> &corners = mesh.faceVertexIndices();
    const double(*m)[4] = node.global_matrix.m;

    Primitive p;
    for (const auto &v : mesh.points) {
      // USD's matrices act on row vectors: p' = p M.
      double x = v[0] * m[0][0] + v[1] * m[1][0] + v[2] * m[2][0] + m[3][0];
      double y = v[0] * m[0][1] + v[1] * m[1][1] + v[2] * m[2][1] + m[3][1];
      double z = v[0] * m[0][2] + v[1] * m[1][2] + v[2] * m[2][2] + m[3][2];
      double gx = x, gy = frame.zUp ? z : y, gz = frame.zUp ? -y : z;
      float q[3] = {static_cast<float>(gx * frame.metres), static_cast<float>(gy * frame.metres),
                    static_cast<float>(gz * frame.metres)};
      for (int k = 0; k < 3; k++) {
        p.positions.push_back(q[k]);
        p.min[k] = std::fmin(p.min[k], q[k]);
        p.max[k] = std::fmax(p.max[k], q[k]);
      }
    }

    // Triangulated by Tydra; a face of any other count is fanned here, so nothing is dropped silently.
    size_t at = 0;
    uint32_t n = static_cast<uint32_t>(mesh.points.size());
    for (uint32_t count : counts) {
      if (at + count > corners.size()) break;
      for (uint32_t k = 1; k + 1 < count; k++) {
        uint32_t a = corners[at], b = corners[at + k], c = corners[at + k + 1];
        if (a < n && b < n && c < n) {
          p.indices.push_back(a);
          // A left-handed mesh winds the other way.
          p.indices.push_back(mesh.is_rightHanded ? b : c);
          p.indices.push_back(mesh.is_rightHanded ? c : b);
        }
      }
      at += count;
    }

    const tinyusdz::value::color3f &shown = mesh.displayColor;
    p.color[0] = shown[0]; p.color[1] = shown[1]; p.color[2] = shown[2];
    if (mesh.material_id >= 0 && static_cast<size_t>(mesh.material_id) < scene.materials.size()) {
      const auto &diffuse = scene.materials[static_cast<size_t>(mesh.material_id)].surfaceShader.diffuseColor;
      if (!diffuse.is_texture()) {
        p.color[0] = diffuse.value[0]; p.color[1] = diffuse.value[1]; p.color[2] = diffuse.value[2];
      }
    }

    if (!p.indices.empty()) out->push_back(std::move(p));
  }

  for (const auto &child : node.children) Place(scene, child, frame, out);
}

std::string Number(double v) {
  std::ostringstream s;
  s.precision(9);
  s << (std::isfinite(v) ? v : 0.0);
  return s.str();
}

bool WriteGlb(const std::vector<Primitive> &prims, const std::string &path) {
  std::vector<uint8_t> bin;
  std::ostringstream views, accessors, materials, primitives;
  size_t view = 0;
  for (size_t i = 0; i < prims.size(); i++) {
    const Primitive &p = prims[i];
    size_t posAt = bin.size(), posLen = p.positions.size() * 4;
    bin.resize(posAt + posLen);
    std::memcpy(bin.data() + posAt, p.positions.data(), posLen);
    size_t idxAt = bin.size(), idxLen = p.indices.size() * 4;
    bin.resize(idxAt + idxLen);
    std::memcpy(bin.data() + idxAt, p.indices.data(), idxLen);

    const char *sep = i ? "," : "";
    views << sep << "{\"buffer\":0,\"byteOffset\":" << posAt << ",\"byteLength\":" << posLen << ",\"target\":34962},"
          << "{\"buffer\":0,\"byteOffset\":" << idxAt << ",\"byteLength\":" << idxLen << ",\"target\":34963}";
    accessors << sep << "{\"bufferView\":" << view << ",\"componentType\":5126,\"count\":" << p.positions.size() / 3
              << ",\"type\":\"VEC3\",\"min\":[" << Number(p.min[0]) << "," << Number(p.min[1]) << "," << Number(p.min[2])
              << "],\"max\":[" << Number(p.max[0]) << "," << Number(p.max[1]) << "," << Number(p.max[2]) << "]},"
              << "{\"bufferView\":" << view + 1 << ",\"componentType\":5125,\"count\":" << p.indices.size()
              << ",\"type\":\"SCALAR\"}";
    materials << sep << "{\"pbrMetallicRoughness\":{\"baseColorFactor\":[" << Number(p.color[0]) << ","
              << Number(p.color[1]) << "," << Number(p.color[2]) << ",1],\"metallicFactor\":0,\"roughnessFactor\":1}}";
    primitives << sep << "{\"attributes\":{\"POSITION\":" << view << "},\"indices\":" << view + 1
               << ",\"material\":" << i << "}";
    view += 2;
  }

  std::string json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"graticula usd2glb\"},\"scene\":0,"
                     "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],\"meshes\":[{\"primitives\":[" +
                     primitives.str() + "]}],\"materials\":[" + materials.str() + "],\"accessors\":[" +
                     accessors.str() + "],\"bufferViews\":[" + views.str() +
                     "],\"buffers\":[{\"byteLength\":" + std::to_string(bin.size()) + "}]}";
  while (json.size() % 4) json += ' ';
  while (bin.size() % 4) bin.push_back(0);

  auto u32 = [](std::ofstream &f, uint32_t v) { f.write(reinterpret_cast<const char *>(&v), 4); };
  std::ofstream f(path, std::ios::binary);
  if (!f) return false;
  u32(f, 0x46546C67u);  // glTF
  u32(f, 2u);
  u32(f, static_cast<uint32_t>(12 + 8 + json.size() + 8 + bin.size()));
  u32(f, static_cast<uint32_t>(json.size()));
  u32(f, 0x4E4F534Au);  // JSON
  f.write(json.data(), static_cast<std::streamsize>(json.size()));
  u32(f, static_cast<uint32_t>(bin.size()));
  u32(f, 0x004E4942u);  // BIN
  f.write(reinterpret_cast<const char *>(bin.data()), static_cast<std::streamsize>(bin.size()));
  return static_cast<bool>(f);
}

}  // namespace

int main(int argc, char **argv) {
  if (argc != 3) {
    std::cerr << "usage: usd2glb <in.usd|usda|usdc|usdz> <out.glb>\n";
    return 2;
  }

  std::string in = argv[1], warn, err;
  tinyusdz::Stage stage;
  if (!tinyusdz::LoadUSDFromFile(in, &stage, &warn, &err)) {
    std::cerr << "The file is not a USD stage this converter reads: " << err << "\n";
    return 1;
  }

  tinyusdz::tydra::RenderScene scene;
  tinyusdz::tydra::RenderSceneConverter converter;
  tinyusdz::tydra::RenderSceneConverterEnv env(stage);
  env.scene_config.load_texture_assets = false;
  env.set_search_paths({tinyusdz::io::GetBaseDir(in)});
  if (!converter.ConvertToRenderScene(env, &scene)) {
    std::cerr << "The stage's meshes could not be read: " << converter.GetError() << "\n";
    return 1;
  }

  // Read from the stage's own metadata: Tydra's scene metadata keeps its defaults (measured on our own box.usda).
  Frame frame;
  frame.zUp = stage.metas().upAxis.get_value() == tinyusdz::Axis::Z;
  double unit = stage.metas().metersPerUnit.get_value();
  frame.metres = std::isfinite(unit) && unit > 0 ? unit : 1.0;

  std::vector<Primitive> prims;
  for (const auto &node : scene.nodes) Place(scene, node, frame, &prims);
  if (prims.empty()) {
    std::cerr << "The stage holds no mesh with a triangle.\n";
    return 1;
  }

  if (!WriteGlb(prims, argv[2])) {
    std::cerr << "The glb could not be written.\n";
    return 1;
  }

  return 0;
}
