# Console test corpus

`box.dae` and `box-ascii.fbx` are one 10 × 20 × 4 m red box. It was written by hand as OBJ and converted by Assimp's
command-line tool (Debian `assimp-utils` 5.2.5, 2026-11-07): `assimp export box.obj box.dae -fcollada` and
`assimp export box.obj box-ascii.fbx -ffbxa`. They are the inputs for the 3D object layer's conversion test
(ADR-282). Nothing in them is anyone else's model.
