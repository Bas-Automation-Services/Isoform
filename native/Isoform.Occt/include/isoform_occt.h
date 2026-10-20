// Isoform OCCT baseline: a minimal C ABI over OpenCASCADE for the B-rep comparison.
// Every function returns 0 on success, non-zero on failure; iso_last_error() describes the failure.
// Faces, edges and vertices are addressed by 0-based index into the shape's indexed maps.
#pragma once

#ifdef _WIN32
#define ISO_API extern "C" __declspec(dllexport)
#else
#define ISO_API extern "C" __attribute__((visibility("default")))
#endif

typedef struct IsoShape IsoShape;
typedef struct IsoOp IsoOp;

enum IsoFaceKind { ISO_PLANE = 0, ISO_CYLINDER = 1, ISO_TORUS = 2, ISO_OTHER = 3 };
enum IsoBoolean { ISO_FUSE = 0, ISO_CUT = 1, ISO_COMMON = 2 };

ISO_API const char* iso_last_error(void);

ISO_API int iso_make_box(double x0, double y0, double z0, double x1, double y1, double z1, IsoShape** out);
// Cylinder from base point p along d, radius r, height h.
ISO_API int iso_make_cylinder(double px, double py, double pz, double dx, double dy, double dz, double r, double h, IsoShape** out);
// Large block with one face on the plane (point, normal), extending along the normal.
ISO_API int iso_make_halfspace(double px, double py, double pz, double nx, double ny, double nz, double size, IsoShape** out);
ISO_API void iso_shape_free(IsoShape* shape);

ISO_API int iso_face_count(IsoShape* shape, int* count);
// data: plane = origin(3), outward normal(3); cylinder = axis point(3), axis(3), radius;
// torus = centre(3), axis(3), major, minor. Kind ISO_OTHER leaves data untouched.
ISO_API int iso_face_info(IsoShape* shape, int face, int* kind, double* data);
ISO_API int iso_face_distance(IsoShape* shape, int face, double x, double y, double z, double* distance);

ISO_API int iso_edge_count(IsoShape* shape, int* count);
// Distinct faces around an edge (seam edges report one face).
ISO_API int iso_edge_faces(IsoShape* shape, int edge, int* faces, int capacity, int* count);
// End vertices of an edge (closed edges report the same vertex twice).
ISO_API int iso_edge_vertices(IsoShape* shape, int edge, int* first, int* last);
ISO_API int iso_vertex_count(IsoShape* shape, int* count);
ISO_API int iso_vertex_faces(IsoShape* shape, int vertex, int* faces, int capacity, int* count);
ISO_API int iso_solid_count(IsoShape* shape, int* count);
ISO_API int iso_is_valid(IsoShape* shape, int* valid);
ISO_API int iso_volume(IsoShape* shape, double* volume);
ISO_API int iso_write_step(IsoShape* shape, const char* utf8_path);
// Tessellates every face (chordal deflection in mm, angular in radians); returns the total triangle count.
ISO_API int iso_tessellate(IsoShape* shape, double deflection, double angle, int* triangles);

// Boolean op on two shapes; simplify merges same-domain faces (history includes the merge).
ISO_API int iso_boolean(int kind, IsoShape* a, IsoShape* b, int simplify, IsoOp** out);
// Constant-radius fillet on the given edges of a shape.
ISO_API int iso_fillet(IsoShape* shape, const int* edges, int edge_count, double radius, IsoOp** out);
ISO_API int iso_op_result(IsoOp* op, IsoShape** out);
// Result faces that an argument face became: itself, its modified pieces, or nothing if deleted.
ISO_API int iso_op_image(IsoOp* op, int argument, int face, int* faces, int capacity, int* count);
// Result faces generated from edges of an argument (e.g. fillet faces).
ISO_API int iso_op_generated(IsoOp* op, int argument, int* faces, int capacity, int* count);
ISO_API void iso_op_free(IsoOp* op);
