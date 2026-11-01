"""A sparse Cholesky factor for a symmetric positive definite system on a grid, ordered by nested dissection and
computed by the multifrontal method (D-333).

Topo to Raster's fit (``_topo._solve``) is a symmetric positive definite system whose unknowns are a grid's cells and
whose couplings reach a few cells. SuperLU factored it, but SuperLU sets aside room for the factor in proportion to the
matrix's entries before it knows the fill — about 1.46 GB on a 400 x 400 grid whose factor held 33 million entries —
which is what put Topo to Raster past ADR-223's memory limit there. This factor holds only what it computes.

The method is textbook and is cited, not invented:

- **Nested dissection** (George 1973, SIAM J. Numer. Anal. 10(2)): the grid is cut by a band of rows or columns as wide
  as the couplings reach, the two halves are ordered before the band, and each half is cut again, down to small boxes.
- **The multifrontal method** (Duff and Reid 1983, ACM TOMS 9(3); Liu 1992, SIAM Review 34(1)): each node of the
  dissection tree gathers its rows of the matrix and its children's update matrices into a dense front, factors its own
  unknowns, and passes the Schur complement on the unknowns it borders to its parent. Fronts are dense, so the work is
  LAPACK's and BLAS's.

The answer is the system's solution to rounding, as SuperLU's is: a different order of elimination, so not the same
bits (``test_performance_answers.Topo``)."""
from __future__ import annotations

import numpy as np

#: A box of at most this many cells is not cut again (D-333): smaller leaves mean more nodes and more Python overhead,
#: larger ones dense fronts that hold zeros.
LEAF_CELLS = 36


def _reach(matrix, columns):
    """How many cells the matrix's couplings reach across or down: the largest Chebyshev distance of a nonzero."""
    coo = matrix.tocoo()
    if coo.nnz == 0:
        return 0
    dr = np.abs(coo.row // columns - coo.col // columns)
    dc = np.abs(coo.row % columns - coo.col % columns)
    return int(max(dr.max(), dc.max()))


class GridCholesky:
    """L·Lᵀ of a symmetric positive definite ``matrix`` whose unknown k is the cell (k // columns, k % columns) of a
    ``rows`` x ``columns`` grid. ``solve(b)`` answers A⁻¹b. Raises ``ValueError`` if a front is not positive definite.
    """

    def __init__(self, matrix, rows, columns):
        from scipy.linalg import lapack
        a = matrix.tocsr()
        a.sum_duplicates()
        n = rows * columns
        if a.shape != (n, n):
            raise ValueError("the matrix is not the grid's")
        self.n = n
        width = max(1, _reach(a, columns))
        indptr, indices, data = a.indptr, a.indices, a.data
        # The dissection tree, in postorder: each node (r0, r1, c0, c1) is a box; its own unknowns are the band that
        # cuts it, or the whole box at a leaf; its children are the two boxes on either side of the band.
        nodes = []

        def dissect(r0, r1, c0, c1):
            h, w = r1 - r0, c1 - c0
            if h <= 0 or w <= 0:
                return None
            kids = []
            if h * w <= LEAF_CELLS or max(h, w) < 2 * width + 2:
                own = (np.arange(r0, r1)[:, None] * columns + np.arange(c0, c1)[None, :]).ravel()
            elif h >= w:
                mid = r0 + (h - width) // 2
                kids = [dissect(r0, mid, c0, c1), dissect(mid + width, r1, c0, c1)]
                own = (np.arange(mid, mid + width)[:, None] * columns + np.arange(c0, c1)[None, :]).ravel()
            else:
                mid = c0 + (w - width) // 2
                kids = [dissect(r0, r1, c0, mid), dissect(r0, r1, mid + width, c1)]
                own = (np.arange(r0, r1)[:, None] * columns + np.arange(mid, mid + width)[None, :]).ravel()
            nodes.append(((r0, r1, c0, c1), own, [k for k in kids if k is not None]))
            return len(nodes) - 1

        dissect(0, rows, 0, columns)
        position = np.full(n, -1, np.int64)
        updates = {}          # a child's index -> (its border, its update matrix), until its parent takes it
        self.fronts = []      # per node, in postorder: (own unknowns, border unknowns, L of own, L of border ᵀ)
        for k, ((r0, r1, c0, c1), own, kids) in enumerate(nodes):
            # The node's rows of the matrix, their columns as global indices.
            starts, ends = indptr[own], indptr[own + 1]
            lengths = ends - starts
            gather = np.repeat(starts - np.cumsum(lengths) + lengths, lengths) + np.arange(int(lengths.sum()))
            cols = indices[gather]
            vals = data[gather]
            local = np.repeat(np.arange(len(own)), lengths)
            # Its border: the unknowns outside its box that its own rows or its children's borders touch.
            r, c = cols // columns, cols % columns
            outside = (r < r0) | (r >= r1) | (c < c0) | (c >= c1)
            border = [cols[outside]] + [updates[j][0] for j in kids]
            border = np.unique(np.concatenate(border)) if border else np.zeros(0, np.int64)
            if len(border):
                br, bc = border // columns, border % columns
                border = border[(br < r0) | (br >= r1) | (bc < c0) | (bc >= c1)]
            m, nb = len(own), len(border)
            position[own] = np.arange(m)
            position[border] = m + np.arange(nb)
            front = np.zeros((m + nb, m + nb))
            p = position[cols]
            keep = p >= 0            # columns in a child's box were assembled at that child
            front[local[keep], p[keep]] = vals[keep]
            front[m:, :m] = front[:m, m:].T
            for j in kids:
                child_border, update = updates.pop(j)
                at = position[child_border]
                front[np.ix_(at, at)] += update
            position[own] = -1
            position[border] = -1
            lower, info = lapack.dpotrf(front[:m, :m], lower=1, clean=1, overwrite_a=0)
            if info != 0:
                raise ValueError("a front is not positive definite")
            side = lapack.dtrtrs(lower, front[:m, m:], lower=1)[0] if nb else np.zeros((m, 0))
            if nb:
                update = np.ascontiguousarray(front[m:, m:])     # a copy, so the front itself is let go
                update -= side.T @ side
                updates[k] = (border, update)
            del front
            self.fronts.append((own, border, lower, side))

    def entries(self):
        """How many numbers the factor holds."""
        return sum(lo.size + side.size for _, _, lo, side in self.fronts)

    def solve(self, b):
        from scipy.linalg.lapack import dtrtrs
        x = np.array(b, dtype=float, copy=True)
        for own, border, lower, side in self.fronts:
            y = dtrtrs(lower, x[own], lower=1)[0]
            x[own] = y
            if len(border):
                x[border] -= side.T @ y
        for own, border, lower, side in reversed(self.fronts):
            z = x[own]
            if len(border):
                z = z - side @ x[border]
            x[own] = dtrtrs(lower, z, lower=1, trans=1)[0]
        return x
