"""The point cloud networks of arcpy.ddd's deep learning tools — ADR-249. Written in plain PyTorch from their papers,
without torch_geometric, torch_scatter, spconv or any CUDA-only operator, so that they train and run on the CPU builds
the server image carries (ADR-248). Every network starts from random weights; grapy downloads nothing.

==========================  ============================================================================================
Tool's architecture value   Network and source
==========================  ============================================================================================
RANDLANET                   RandLA-Net: random sampling, local spatial encoding and attentive pooling in dilated
                            residual blocks, nearest-neighbour upsampling (Hu et al. 2020, CVPR, §3 and Fig. 3, 7)
SQN                         SQN: RandLA-Net's encoder with the point query network, each queried point's features
                            interpolated from every encoder level by its 3 nearest neighbours (Hu et al. 2022, ECCV, §3)
POINTCNN                    PointCNN: X-Conv, the X-transformation learned from the neighbours' local coordinates
                            (Li et al. 2018, NeurIPS, Algorithm 1), with X-Conv decoder layers for segmentation (§3.3)
POINT_TRANSFORMER_V3        Point Transformer V3: serialised patch attention over z-order and Hilbert orders and their
                            transposes, shuffled; xCPE (a submanifold sparse convolution); grid pooling and unpooling;
                            pre-norm blocks (Wu et al. 2024, CVPR, §4). Hilbert codes by Skilling 2004 (AIP Conf.
                            Proc. 707). The sparse convolutions are computed exactly on the occupied voxels, by
                            looking each neighbour up among the sorted voxel keys.
SECD (object detection)     SECOND: voxel feature encoding, a sparse 3D middle extractor of submanifold and regular
                            sparse convolutions, a region proposal network on the bird's-eye view, anchors per object
                            code, focal loss and the sine-error angle loss (Yan, Mao and Li 2018, Sensors 18(10)
                            3337; VFE from Zhou and Tuzel 2018, CVPR; focal loss Lin et al. 2017, ICCV). The sparse
                            convolutions are dense 3D convolutions masked to the active sites, which computes a
                            submanifold convolution exactly and a regular sparse one exactly at its active outputs;
                            batch normalisation runs over the active sites only.
==========================  ============================================================================================

What the papers leave to their code is INFERRED (ADR-249 §5.3): each network's widths are the papers' (RandLA-Net's
16-64-128-256 with K 16 and sampling ratio 4; PointCNN's 64-128-256-384; PTv3's 32-64-128-256-512 encoder with depths
2-2-2-6-2 and a 64-64-128-256 decoder; SQN's 256-128-64 query head), and ``width`` scales them (the tests use a
fraction). Random sampling takes a block's first points: the blocks are shuffled when read, so this is random sampling
and an inference repeats.
"""
from __future__ import annotations

import functools
import math

import numpy as np

from . import _deeplearning as dl

CLASSIFIERS = ("RANDLANET", "SQN", "POINTCNN", "POINT_TRANSFORMER_V3")
DETECTORS = ("SECD",)
PAPERS = {
    "RANDLANET": "RandLA-Net, Hu et al. 2020, CVPR",
    "SQN": "SQN, Hu et al. 2022, ECCV",
    "POINTCNN": "PointCNN, Li et al. 2018, NeurIPS",
    "POINT_TRANSFORMER_V3": "Point Transformer V3, Wu et al. 2024, CVPR",
    "SECD": "SECOND, Yan, Mao and Li 2018, Sensors 18(10) 3337",
}


# ---- serialisation: the space-filling curves of Point Transformer V3 (numpy, int64)


def z_order(coords, bits=16):
    """Morton codes of non-negative integer (x, y, z) — bit k of x, y and z at 3k+2, 3k+1 and 3k."""
    c = np.asarray(coords, np.int64)
    code = np.zeros(len(c), np.int64)
    for k in range(bits):
        code |= ((c[:, 0] >> k) & 1) << (3 * k + 2)
        code |= ((c[:, 1] >> k) & 1) << (3 * k + 1)
        code |= ((c[:, 2] >> k) & 1) << (3 * k)
    return code


def hilbert(coords, bits=16):
    """Hilbert codes of non-negative integer (x, y, z): Skilling's transform of the axes to the transposed Hilbert index
    (Skilling 2004, *Programming the Hilbert curve*, AIP Conf. Proc. 707, 381–387), its bits then interleaved."""
    x = [np.asarray(coords, np.int64)[:, i].copy() for i in range(3)]
    q = 1 << (bits - 1)
    while q > 1:
        p = q - 1
        for i in range(3):
            high = (x[i] & q) != 0
            x[0] = np.where(high, x[0] ^ p, x[0])
            t = np.where(high, 0, (x[0] ^ x[i]) & p)
            x[0] ^= t
            x[i] ^= t
        q >>= 1
    for i in range(1, 3):
        x[i] ^= x[i - 1]
    t = np.zeros_like(x[0])
    q = 1 << (bits - 1)
    while q > 1:
        t = np.where((x[2] & q) != 0, t ^ (q - 1), t)
        q >>= 1
    for i in range(3):
        x[i] ^= t
    code = np.zeros_like(x[0])
    for k in range(bits - 1, -1, -1):
        for i in range(3):
            code = (code << 1) | ((x[i] >> k) & 1)
    return code


ORDERS = ("z", "z-trans", "hilbert", "hilbert-trans")


def serialise(coords, order, bits=16):
    c = np.asarray(coords, np.int64)
    if order.endswith("-trans"):
        c = c[:, [1, 0, 2]]
    return (hilbert if order.startswith("hilbert") else z_order)(c, bits)


# ---- the networks


@functools.lru_cache(maxsize=None)
def nets():
    torch = dl.framework("arcpy.ddd")
    nn = torch.nn
    F = torch.nn.functional

    def knn(query, support, k):
        """Indexes (B, M, k) and distances of each query point's k nearest support points, by a k-d tree of each
        sample's support (scipy). Measured on 8,192-point blocks of real lidar (D-341), the brute-force distances
        and top-k this replaced were seven tenths of RandLA-Net's forward pass. The neighbours are the same; among
        points at exactly equal distance either may be taken, which a network that pools over its neighbours does
        not see."""
        from scipy.spatial import cKDTree
        k = min(int(k), support.shape[1])
        q = query.detach().cpu().numpy()
        s = support.detach().cpu().numpy()
        found = np.empty((q.shape[0], q.shape[1], k), np.int64)
        far = np.empty((q.shape[0], q.shape[1], k), np.float64)
        for b in range(q.shape[0]):
            d, i = cKDTree(s[b]).query(q[b], k=k)
            found[b], far[b] = i.reshape(q.shape[1], k), d.reshape(q.shape[1], k)
        return (torch.from_numpy(found).to(query.device),
                torch.from_numpy(far).to(device=query.device, dtype=query.dtype))

    def gather(x, idx):
        """x (B, N, C) at idx (B, M, k) → (B, M, k, C)."""
        b = torch.arange(x.shape[0], device=x.device).view(-1, *([1] * (idx.dim() - 1)))
        return x[b, idx]

    class Dense(nn.Module):
        """A shared MLP layer over the last axis: linear, batch norm over every point, LeakyReLU 0.2 (RandLA-Net's)."""

        def __init__(self, i, o, act=True, bn=True, slope=0.2):
            super().__init__()
            self.lin = nn.Linear(i, o, bias=not bn)
            self.bn = nn.BatchNorm1d(o) if bn else None
            self.act, self.slope = act, slope

        def forward(self, x):
            y = self.lin(x)
            if self.bn is not None:
                shape = y.shape
                y = self.bn(y.reshape(-1, shape[-1])).reshape(shape)
            return F.leaky_relu(y, self.slope) if self.act else y

    def sizes(n, levels, ratio, floor=2):
        out = [n]
        for _ in range(levels):
            out.append(max(min(floor, out[-1]), out[-1] // ratio))
        return out

    # ---- RandLA-Net (Hu et al. 2020)

    class AttentivePooling(nn.Module):
        def __init__(self, d_in, d_out):
            super().__init__()
            self.score = nn.Linear(d_in, d_in, bias=False)
            self.mlp = Dense(d_in, d_out)

        def forward(self, f):                       # f (B, N, K, d)
            s = torch.softmax(self.score(f), dim=2)
            return self.mlp((s * f).sum(2))

    class Residual(nn.Module):
        """The dilated residual block: two local spatial encodings and attentive poolings (Hu et al. 2020, Fig. 3)."""

        def __init__(self, d_in, d_out):
            super().__init__()
            half = d_out // 2
            self.mlp1 = Dense(d_in, half)
            self.pos1 = Dense(10, half)
            self.pool1 = AttentivePooling(d_out, half)
            self.pos2 = Dense(half, half)
            self.pool2 = AttentivePooling(d_out, d_out)
            self.mlp2 = Dense(d_out, 2 * d_out, act=False)
            self.short = Dense(d_in, 2 * d_out, act=False)

        def forward(self, xyz, f, idx):
            near = gather(xyz, idx)
            centre = xyz.unsqueeze(2).expand_as(near)
            rel = centre - near
            geo = torch.cat([centre, near, rel, rel.norm(dim=-1, keepdim=True)], -1)    # 10 = 3 + 3 + 3 + 1
            g = self.pos1(geo)
            h = self.mlp1(f)
            h = self.pool1(torch.cat([gather(h, idx), g], -1))
            g = self.pos2(g)
            h = self.pool2(torch.cat([gather(h, idx), g], -1))
            return F.leaky_relu(self.mlp2(h) + self.short(f), 0.2)

    class RandLANet(nn.Module):
        def __init__(self, features, classes, width=1.0, k=16, ratio=4, query=False):
            super().__init__()
            widths = [max(4, int(round(w * width)) // 2 * 2) for w in (16, 64, 128, 256)]
            self.k, self.ratio, self.query = k, ratio, query
            self.fc0 = Dense(3 + features, 8)
            ins = [8] + [2 * w for w in widths[:-1]]
            self.encoder = nn.ModuleList(Residual(i, o) for i, o in zip(ins, widths))
            skip = [2 * widths[0]] + [2 * w for w in widths]
            if query:
                # SQN's point query network: every level's features, interpolated at the query, then 256-128-64.
                q = [max(8, int(round(w * width))) for w in (256, 128, 64)]
                self.head = nn.Sequential(Dense(sum(skip), q[0]), Dense(q[0], q[1]), Dense(q[1], q[2]),
                                          nn.Dropout(0.5), nn.Linear(q[2], classes))
            else:
                self.mid = Dense(skip[-1], skip[-1])
                self.decoder = nn.ModuleList()
                d = skip[-1]
                for level in range(len(widths) - 1, -1, -1):
                    out = skip[level]
                    self.decoder.append(Dense(d + out, out))
                    d = out
                f1, f2 = max(8, int(round(64 * width))), max(8, int(round(32 * width)))
                self.head = nn.Sequential(Dense(d, f1), Dense(f1, f2), nn.Dropout(0.5), nn.Linear(f2, classes))

        def forward(self, xyz, feats):
            f = self.fc0(torch.cat([xyz, feats], -1))
            n = sizes(xyz.shape[1], len(self.encoder), self.ratio)
            points, skips = [xyz], []
            for level, block in enumerate(self.encoder):
                here = points[-1]
                idx, _ = knn(here, here, self.k)
                f = block(here, f, idx)
                if level == 0:
                    skips.append(f)
                f = gather(f, idx[:, :n[level + 1]]).max(2).values       # random sampling: the first points
                points.append(here[:, :n[level + 1]])
                skips.append(f)
            if self.query:
                parts = []
                for level, feature in enumerate(skips):
                    idx, dist = knn(xyz, points[level], 3)
                    w = 1.0 / (dist + 1e-8)
                    w = w / w.sum(-1, keepdim=True)
                    parts.append((gather(feature, idx) * w.unsqueeze(-1)).sum(2))
                return self.head(torch.cat(parts, -1))
            f = self.mid(skips[-1])
            for step, level in enumerate(range(len(self.encoder) - 1, -1, -1)):
                idx, _ = knn(points[level], points[level + 1], 1)
                up = gather(f, idx)[:, :, 0]
                f = self.decoder[step](torch.cat([skips[level], up], -1))
            return self.head(f)

    # ---- PointCNN (Li et al. 2018)

    class XConv(nn.Module):
        """X-Conv (Li et al. 2018, Algorithm 1): local coordinates lifted by MLP_δ, the K × K X-transformation learned
        from them, applied to the neighbours' features, then a depthwise separable convolution over the K neighbours."""

        def __init__(self, c_in, c_out, k, dilation, first=False):
            super().__init__()
            self.k, self.dilation = k, dilation
            c_delta = c_out // 2 if first else c_out // 4
            self.delta = nn.Sequential(Dense(3, c_delta, slope=0.0), Dense(c_delta, c_delta, slope=0.0))
            self.x = nn.Sequential(nn.Linear(3 * k, k * k), nn.ELU(), nn.Linear(k * k, k * k), nn.ELU(),
                                   nn.Linear(k * k, k * k))
            c_star = c_delta + c_in
            self.multiplier = max(1, math.ceil(c_out / c_star))
            self.depthwise = nn.Parameter(torch.randn(k, c_star, self.multiplier) / math.sqrt(k))
            self.pointwise = Dense(c_star * self.multiplier, c_out, slope=0.0)

        def forward(self, rep, xyz, feats):
            idx, _ = knn(rep, xyz, self.k * self.dilation)
            idx = idx[:, :, ::self.dilation][:, :, :self.k]
            k = idx.shape[-1]
            local = gather(xyz, idx) - rep.unsqueeze(2)
            f = self.delta(local)
            if feats is not None:
                f = torch.cat([f, gather(feats, idx)], -1)
            if k < self.k:                  # fewer points than K: pad the neighbourhood with its own first point
                pad = self.k - k
                local = torch.cat([local, local[:, :, :1].expand(-1, -1, pad, -1)], 2)
                f = torch.cat([f, f[:, :, :1].expand(-1, -1, pad, -1)], 2)
            b, p = local.shape[:2]
            x = self.x(local.reshape(b, p, -1)).reshape(b, p, self.k, self.k)
            fx = torch.matmul(x, f)
            out = torch.einsum("bpkc,kcm->bpcm", fx, self.depthwise).reshape(b, p, -1)
            return self.pointwise(out)

    class PointCNN(nn.Module):
        def __init__(self, features, classes, width=1.0):
            super().__init__()
            c = [max(8, int(round(w * width))) for w in (64, 128, 256, 384)]
            self.fractions = (1.0, 3 / 8, 3 / 16, 1 / 16)
            self.encoder = nn.ModuleList([XConv(features, c[0], 8, 1, first=True), XConv(c[0], c[1], 12, 2),
                                          XConv(c[1], c[2], 16, 2), XConv(c[2], c[3], 16, 4)])
            self.decoder = nn.ModuleList([XConv(c[3], c[2], 16, 4), XConv(c[2], c[1], 12, 2),
                                          XConv(c[1], c[0], 8, 2), XConv(c[0], c[0], 8, 1)])
            self.fuse = nn.ModuleList([Dense(2 * c[2], c[2], slope=0.0), Dense(2 * c[1], c[1], slope=0.0),
                                       Dense(2 * c[0], c[0], slope=0.0), Dense(2 * c[0], c[0], slope=0.0)])
            self.head = nn.Sequential(Dense(c[0], c[0], slope=0.0), nn.Dropout(0.5), nn.Linear(c[0], classes))
            self.features = features

        def forward(self, xyz, feats):
            n = xyz.shape[1]
            counts = [max(2, min(n, int(round(n * f)))) for f in self.fractions]
            points, skips = [], []
            here, f = xyz, (feats if self.features else None)
            for level, layer in enumerate(self.encoder):
                rep = xyz[:, :counts[level]]
                f = layer(rep, here, f)
                points.append(rep)
                skips.append(f)
                here = rep
            targets = [2, 1, 0, 0]
            for layer, fuse, level in zip(self.decoder, self.fuse, targets):
                rep = points[level]
                f = layer(rep, here, f)
                f = fuse(torch.cat([f, skips[level]], -1))
                here = rep
            return self.head(f)

    # ---- Point Transformer V3 (Wu et al. 2024)

    class Sparse:
        """Occupied voxels: integer coordinates (M, 3), the sample each belongs to, and keys to look neighbours up."""

        def __init__(self, coords, batch):
            self.coords, self.batch = coords, batch
            self.span = int(coords.max().item()) + 8 if len(coords) else 8
            self.keys = self.key(coords)
            self.sorted, self.order = torch.sort(self.keys)

        def key(self, coords):
            s = self.span
            shifted = coords + 4
            return ((self.batch * s + shifted[:, 0]) * s + shifted[:, 1]) * s + shifted[:, 2]

        def neighbours(self, offset):
            """The occupied voxel at ``offset`` from each, and whether there is one; kept, since every block of a
            stage asks the same."""
            cache = self.__dict__.setdefault("_cache", {})
            name = tuple(int(v) for v in offset)
            if name not in cache:
                wanted = self.key(self.coords + offset)
                pos = torch.searchsorted(self.sorted, wanted).clamp(max=len(self.sorted) - 1)
                found = self.sorted[pos] == wanted
                j = self.order[pos]
                cache[name] = (j[found], found) if found.any() else (None, found)
            return cache[name]

    class SubManifold(nn.Module):
        """A submanifold sparse convolution (Graham, Engelcke and van der Maaten 2018): outputs only at occupied voxels,
        summing each kernel offset's weights over the occupied neighbours at that offset."""

        def __init__(self, c_in, c_out, k, bias=True):
            super().__init__()
            r = k // 2
            grid = torch.stack(torch.meshgrid(*(torch.arange(-r, r + 1),) * 3, indexing="ij"), -1).reshape(-1, 3)
            self.register_buffer("offsets", grid, persistent=False)
            self.weight = nn.Parameter(torch.randn(len(grid), c_in, c_out) * math.sqrt(2.0 / (c_in * len(grid))))
            self.bias = nn.Parameter(torch.zeros(c_out)) if bias else None

        def pairs(self, sparse):
            """(output voxel, source voxel x offsets + offset) of every occupied neighbour, kept as ``neighbours``
            is."""
            cache = sparse.__dict__.setdefault("_cache", {})
            name = ("pairs", tuple(tuple(int(v) for v in o) for o in self.offsets))
            if name not in cache:
                k, rows, sources = len(self.offsets), [], []
                for o in range(k):
                    j, found = sparse.neighbours(self.offsets[o])
                    if j is not None:
                        rows.append(torch.nonzero(found).reshape(-1))
                        sources.append(j * k + o)
                cache[name] = ((torch.cat(rows), torch.cat(sources)) if rows else
                               (torch.zeros(0, dtype=torch.long), torch.zeros(0, dtype=torch.long)))
            return cache[name]

        def forward(self, f, sparse):
            # Every voxel through every offset's weights in one product, then each output summing its occupied
            # neighbours' terms in one index_add: the same sum as offset by offset with masked scatters, whose
            # backward filled and scattered a tensor of the input's size per offset — 40 % of Point Transformer
            # V3's training step on 8,192-point blocks of real lidar (D-341).
            # A layer whose product would pass 64 MB (the 125-offset stem) keeps the offset-by-offset sum, which
            # holds less.
            k, c_in, c_out = self.weight.shape
            if f.shape[0] * k * c_out * f.element_size() > 64 * 2 ** 20:
                out = f.new_zeros(f.shape[0], c_out)
                for o in range(k):
                    j, found = sparse.neighbours(self.offsets[o])
                    if j is not None:
                        out[found] += f[j] @ self.weight[o]
            else:
                rows, sources = self.pairs(sparse)
                every = (f @ self.weight.permute(1, 0, 2).reshape(c_in, k * c_out)).reshape(-1, c_out)
                out = f.new_zeros(f.shape[0], c_out).index_add_(0, rows, every.index_select(0, sources))
            return out + self.bias if self.bias is not None else out

    class Block(nn.Module):
        """A PTv3 block: x + xCPE(x); x + attention(LN(x)) over serialised patches; x + MLP(LN(x))."""

        def __init__(self, c, heads, patch):
            super().__init__()
            self.cpe = SubManifold(c, c, 3)
            self.cpe_linear = nn.Linear(c, c)
            self.cpe_norm = nn.LayerNorm(c)
            self.norm1, self.norm2 = nn.LayerNorm(c), nn.LayerNorm(c)
            self.qkv, self.proj = nn.Linear(c, 3 * c), nn.Linear(c, c)
            self.mlp = nn.Sequential(nn.Linear(c, 4 * c), nn.GELU(), nn.Linear(4 * c, c))
            self.heads, self.patch = heads, patch

        def attend(self, x, orders):
            out = torch.empty_like(x)
            c = x.shape[1]
            for order in orders:                    # one sample's tokens, in the curve's order
                n = len(order)
                size = min(self.patch, n)
                pad = (-n) % size
                index = torch.cat([order, order[n - pad:]]) if pad else order
                t = x[index].reshape(-1, size, c)
                q, k, v = self.qkv(t).reshape(t.shape[0], size, 3, self.heads, c // self.heads).permute(2, 0, 3, 1, 4)
                y = F.scaled_dot_product_attention(q, k, v).transpose(1, 2).reshape(-1, c)
                out[order] = self.proj(y[:n])
            return out

        def forward(self, x, sparse, orders):
            x = x + self.cpe_norm(self.cpe_linear(self.cpe(x, sparse)))
            x = x + self.attend(self.norm1(x), orders)
            return x + self.mlp(self.norm2(x))

    def norm_act(c):
        return nn.Sequential(nn.BatchNorm1d(c), nn.GELU())

    class PTv3(nn.Module):
        def __init__(self, features, classes, width=1.0, patch=1024, grid=64, depths=(2, 2, 2, 6, 2),
                     decoder_depths=(2, 2, 2, 2)):
            super().__init__()
            enc = [max(8, int(round(w * width)) // 8 * 8) for w in (32, 64, 128, 256, 512)]
            dec = [max(8, int(round(w * width)) // 8 * 8) for w in (64, 64, 128, 256)]
            heads = [max(1, h) for h in (2, 4, 8, 16, 32)]
            dec_heads = (4, 4, 8, 16)
            self.grid, self.depths = grid, depths
            self.stem = SubManifold(3 + features, enc[0], 5)
            self.stem_norm = norm_act(enc[0])
            self.pool = nn.ModuleList([nn.Linear(enc[s - 1], enc[s]) for s in range(1, len(enc))])
            self.pool_norm = nn.ModuleList([norm_act(enc[s]) for s in range(1, len(enc))])
            self.encoder = nn.ModuleList(
                nn.ModuleList(Block(enc[s], self._heads(enc[s], heads[s]), patch) for _ in range(depths[s]))
                for s in range(len(enc)))
            self.up = nn.ModuleList()
            self.up_skip = nn.ModuleList()
            self.decoder = nn.ModuleList()
            below = enc[-1]
            for s in range(len(enc) - 2, -1, -1):
                self.up.append(nn.Sequential(nn.Linear(below, dec[s]), norm_act(dec[s])))
                self.up_skip.append(nn.Sequential(nn.Linear(enc[s], dec[s]), norm_act(dec[s])))
                self.decoder.append(nn.ModuleList(Block(dec[s], self._heads(dec[s], dec_heads[s]), patch)
                                                  for _ in range(decoder_depths[s])))
                below = dec[s]
            self.head = nn.Linear(dec[0], classes)

        @staticmethod
        def _heads(c, wanted):
            h = max(1, min(wanted, c // 8))
            while c % h:
                h -= 1
            return h

        def _orders(self, coords, batch, samples):
            names = list(ORDERS)
            if self.training:                       # shuffle order (§4.2)
                names = [names[i] for i in torch.randperm(len(names)).tolist()]
            c = coords.cpu().numpy()
            b = batch.cpu().numpy()
            out = []
            for name in names:
                code = serialise(c, name)
                rank = np.lexsort((code, b))
                per = [torch.from_numpy(rank[b[rank] == s]).to(coords.device) for s in range(samples)]
                out.append([p for p in per if len(p)])
            return out

        def forward(self, xyz, feats):
            torch_ = torch
            b, n = xyz.shape[:2]
            lo = xyz.amin(1, keepdim=True)
            cell = (xyz.amax(1, keepdim=True) - lo).amax(-1, keepdim=True).clamp(min=1e-6) / (self.grid - 1)
            coords = torch_.floor((xyz - lo) / cell).long().reshape(-1, 3)
            batch = torch_.arange(b, device=xyz.device).repeat_interleave(n)
            keys = (batch * (self.grid + 1) + coords[:, 0]) * (self.grid + 1) ** 2 + coords[:, 1] * (self.grid + 1) \
                + coords[:, 2]
            unique, inverse = torch_.unique(keys, return_inverse=True)
            m = len(unique)
            source = torch_.cat([xyz, feats], -1).reshape(b * n, -1)
            counts = torch_.zeros(m, device=xyz.device).index_add_(0, inverse, torch_.ones(b * n, device=xyz.device))
            x = torch_.zeros(m, source.shape[1], device=xyz.device).index_add_(0, inverse, source) / counts[:, None]
            vc = torch_.zeros(m, 3, dtype=torch_.long, device=xyz.device).scatter_(0, inverse[:, None].expand(-1, 3),
                                                                                      coords)
            vb = torch_.zeros(m, dtype=torch_.long, device=xyz.device).scatter_(0, inverse, batch)
            sparse = Sparse(vc, vb)
            x = self.stem_norm(self.stem(x, sparse))
            levels, skips, inverses = [sparse], [], []
            for s, blocks in enumerate(self.encoder):
                if s > 0:                           # grid pooling, stride 2: linear, max over the children
                    parent = levels[-1]
                    pc = torch_.div(parent.coords, 2, rounding_mode="floor")
                    pk = (parent.batch * (parent.span + 1) + pc[:, 0]) * (parent.span + 1) ** 2 \
                        + pc[:, 1] * (parent.span + 1) + pc[:, 2]
                    uk, inv = torch_.unique(pk, return_inverse=True)
                    h = self.pool[s - 1](x)
                    pooled = h.new_full((len(uk), h.shape[1]), -torch_.inf).scatter_reduce(
                        0, inv[:, None].expand(-1, h.shape[1]), h, "amax", include_self=True)
                    x = self.pool_norm[s - 1](pooled)
                    child = Sparse(torch_.zeros(len(uk), 3, dtype=torch_.long).scatter_(
                        0, inv[:, None].expand(-1, 3), pc), torch_.zeros(len(uk), dtype=torch_.long).scatter_(
                        0, inv, parent.batch))
                    levels.append(child)
                    inverses.append(inv)
                orders = self._orders(levels[-1].coords, levels[-1].batch, b)
                for k, block in enumerate(blocks):
                    x = block(x, levels[-1], orders[k % len(orders)])
                skips.append(x)
            for step, s in enumerate(range(len(self.encoder) - 2, -1, -1)):
                x = self.up[step](x)[inverses[s]] + self.up_skip[step](skips[s])
                orders = self._orders(levels[s].coords, levels[s].batch, b)
                for k, block in enumerate(self.decoder[step]):
                    x = block(x, levels[s], orders[k % len(orders)])
            return self.head(x)[inverse].reshape(b, n, -1)

    # ---- SECOND (Yan, Mao and Li 2018)

    class VFE(nn.Module):
        """Two VFE layers (32, 128) and a linear layer, max-pooled over each voxel's points (Zhou and Tuzel 2018)."""

        def __init__(self, c_in, width):
            super().__init__()
            a, b = max(8, int(round(32 * width))), max(16, int(round(128 * width)))
            self.l1, self.l2 = nn.Linear(c_in, a // 2), nn.Linear(a, b // 2)
            self.n1, self.n2 = nn.BatchNorm1d(a // 2), nn.BatchNorm1d(b // 2)
            self.out, self.n3 = nn.Linear(b, b), nn.BatchNorm1d(b)
            self.channels = b

        def _layer(self, x, mask, lin, norm):
            v, t, _ = x.shape
            h = F.relu(norm(lin(x).reshape(v * t, -1))).reshape(v, t, -1) * mask
            pooled = h.masked_fill(mask == 0, -1e9).max(1, keepdim=True).values
            return torch.cat([h, pooled.expand_as(h)], -1) * mask

        def forward(self, points, mask):            # points (V, T, c), mask (V, T, 1)
            h = self._layer(points, mask, self.l1, self.n1)
            h = self._layer(h, mask, self.l2, self.n2)
            v, t, _ = h.shape
            h = F.relu(self.n3(self.out(h).reshape(v * t, -1))).reshape(v, t, -1) * mask
            return h.masked_fill(mask == 0, -1e9).max(1).values

    class MaskedNorm(nn.Module):
        def __init__(self, c):
            super().__init__()
            self.bn = nn.BatchNorm1d(c)

        def forward(self, x, mask):                 # x (B, C, D, H, W), mask (B, 1, D, H, W) of 0/1
            out = torch.zeros_like(x)
            active = mask[:, 0] > 0
            if active.any():
                values = x.permute(0, 2, 3, 4, 1)[active]
                out.permute(0, 2, 3, 4, 1)[active] = F.relu(self.bn(values))
            return out

    class Sparse3d(nn.Module):
        """A submanifold (``stride`` None) or regular sparse 3D convolution, as a dense one masked to the active sites."""

        def __init__(self, c_in, c_out, kernel=3, stride=None):
            super().__init__()
            k = (kernel,) * 3 if isinstance(kernel, int) else kernel
            self.submanifold = stride is None
            s = (1, 1, 1) if stride is None else stride
            self.conv = nn.Conv3d(c_in, c_out, k, s, tuple(q // 2 for q in k), bias=False)
            self.norm = MaskedNorm(c_out)
            self.kernel, self.stride = k, s

        def forward(self, x, mask):
            y = self.conv(x)
            if not self.submanifold:
                mask = (F.max_pool3d(mask, self.kernel, self.stride, tuple(q // 2 for q in self.kernel)) > 0).float()
            return self.norm(y * mask, mask), mask

    def conv2d(i, o, s=1):
        return nn.Sequential(nn.Conv2d(i, o, 3, s, 1, bias=False), nn.BatchNorm2d(o), nn.ReLU())

    class Second(nn.Module):
        def __init__(self, point_features, anchors, grid, width=1.0):
            super().__init__()
            nz, ny, nx = grid
            self.grid = grid
            self.vfe = VFE(point_features, width)
            c = [max(8, int(round(w * width))) for w in (16, 32, 64)]
            self.middle = nn.ModuleList([
                Sparse3d(self.vfe.channels, c[0]), Sparse3d(c[0], c[0]),
                Sparse3d(c[0], c[1], 3, (2, 2, 2)), Sparse3d(c[1], c[1]), Sparse3d(c[1], c[1]),
                Sparse3d(c[1], c[2], 3, (2, 1, 1)), Sparse3d(c[2], c[2]), Sparse3d(c[2], c[2]),
                Sparse3d(c[2], c[2], (3, 1, 1), (2, 1, 1))])
            depth = nz
            for stride in (2, 2, 2):
                depth = (depth - 1) // stride + 1
            bev = c[2] * depth
            r1, r2 = max(16, int(round(64 * width))), max(16, int(round(128 * width)))
            self.block1 = nn.Sequential(conv2d(bev, r1), conv2d(r1, r1), conv2d(r1, r1), conv2d(r1, r1))
            self.block2 = nn.Sequential(conv2d(r1, r2, 2), conv2d(r2, r2), conv2d(r2, r2), conv2d(r2, r2))
            self.deconv1 = nn.Sequential(nn.ConvTranspose2d(r1, r2, 1, 1, bias=False), nn.BatchNorm2d(r2), nn.ReLU())
            self.deconv2 = nn.Sequential(nn.ConvTranspose2d(r2, r2, 2, 2, bias=False), nn.BatchNorm2d(r2), nn.ReLU())
            self.anchors = anchors
            self.cls = nn.Conv2d(2 * r2, anchors, 1)
            self.box = nn.Conv2d(2 * r2, anchors * 7, 1)
            nn.init.constant_(self.cls.bias, -math.log((1 - 0.01) / 0.01))     # the focal loss paper's prior

        def forward(self, points, mask, coords, samples):
            """points (V, T, c), mask (V, T, 1), coords (V, 4) of (sample, z, y, x); answers (B, A, H, W) scores and
            (B, A, 7, H, W) box residuals, at half the voxel grid's x and y resolution."""
            nz, ny, nx = self.grid
            v = self.vfe(points, mask)
            dense = v.new_zeros(samples, v.shape[1], nz, ny, nx)
            occupied = v.new_zeros(samples, 1, nz, ny, nx)
            s, z, y, x = coords.unbind(1)
            dense[s, :, z, y, x] = v
            occupied[s, 0, z, y, x] = 1.0
            h, m = dense, occupied
            for layer in self.middle:
                h, m = layer(h, m)
            b, c, d, hh, ww = h.shape
            h = h.reshape(b, c * d, hh, ww)
            x1 = self.block1(h)
            x2 = self.block2(x1)
            up = torch.cat([self.deconv1(x1), self.deconv2(x2)[..., :x1.shape[2], :x1.shape[3]]], 1)
            return self.cls(up), self.box(up).reshape(b, self.anchors, 7, up.shape[2], up.shape[3])

    return {"RANDLANET": RandLANet, "POINTCNN": PointCNN, "POINT_TRANSFORMER_V3": PTv3, "Second": Second,
            "knn": knn, "gather": gather}


def build_classifier(architecture, features, classes, width=1.0, patch=1024):
    """The network an architecture value names, from random weights; ``features`` counts the attributes besides x, y,
    z."""
    n = nets()
    if architecture == "SQN":
        return n["RANDLANET"](features, classes, width, query=True)
    if architecture == "RANDLANET":
        return n["RANDLANET"](features, classes, width)
    if architecture == "POINTCNN":
        return n["POINTCNN"](features, classes, width)
    if architecture == "POINT_TRANSFORMER_V3":
        return n["POINT_TRANSFORMER_V3"](features, classes, width, patch=patch)
    raise ValueError(f"'{architecture}' is not a point cloud classification architecture grapy builds.")


def build_detector(point_features, anchors, grid, width=1.0):
    return nets()["Second"](point_features, anchors, grid, width)
