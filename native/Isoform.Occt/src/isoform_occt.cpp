#include "isoform_occt.h"

#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <Poly_Triangulation.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepTools_History.hxx>
#include <BRep_Tool.hxx>
#include <GProp_GProps.hxx>
#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <Standard_Failure.hxx>
#include <TopExp.hxx>
#include <TopoDS.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_IndexedMapOfShape.hxx>

#include <algorithm>
#include <memory>
#include <set>
#include <string>
#include <vector>

struct IsoShape
{
    TopoDS_Shape shape;
    TopTools_IndexedMapOfShape faces, edges, vertices;
    TopTools_IndexedDataMapOfShapeListOfShape edgeFaces, vertexFaces;

    explicit IsoShape(const TopoDS_Shape& s) : shape(s)
    {
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        TopExp::MapShapes(s, TopAbs_EDGE, edges);
        TopExp::MapShapes(s, TopAbs_VERTEX, vertices);
        TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, edgeFaces);
        TopExp::MapShapesAndAncestors(s, TopAbs_VERTEX, TopAbs_FACE, vertexFaces);
    }
};

struct IsoOp
{
    // Copies of the arguments: their indexed maps match the caller's because MapShapes is deterministic.
    std::vector<std::unique_ptr<IsoShape>> args;
    std::unique_ptr<IsoShape> result;
    Handle(BRepTools_History) history;
};

namespace
{
thread_local std::string lastError;

template <class F> int guard(F&& body)
{
    try
    {
        lastError.clear();
        return body();
    }
    catch (const Standard_Failure& e)
    {
        lastError = std::string(e.DynamicType()->Name()) + ": " + e.GetMessageString();
    }
    catch (const std::exception& e)
    {
        lastError = e.what();
    }
    catch (...)
    {
        lastError = "unknown native failure";
    }
    return 1;
}

int fail(const std::string& message)
{
    lastError = message;
    return 1;
}

int put(IsoShape** out, const TopoDS_Shape& s)
{
    *out = new IsoShape(s);
    return 0;
}

// Distinct ancestors (by IsSame) as 0-based face indices.
int faceIndices(const IsoShape& s, const TopTools_ListOfShape& ancestors, int* out, int capacity, int* count)
{
    std::set<int> found;
    for (const TopoDS_Shape& f : ancestors)
    {
        const int i = s.faces.FindIndex(f);
        if (i > 0) found.insert(i - 1);
    }
    *count = static_cast<int>(found.size());
    if (*count > capacity) return fail("capacity too small");
    int k = 0;
    for (int i : found) out[k++] = i;
    return 0;
}

int resultIndices(const IsoOp& op, const TopTools_ListOfShape& shapes, int* out, int capacity, int* count)
{
    return faceIndices(*op.result, shapes, out, capacity, count);
}
} // namespace

const char* iso_last_error(void) { return lastError.c_str(); }

int iso_make_box(double x0, double y0, double z0, double x1, double y1, double z1, IsoShape** out)
{
    return guard([&] { return put(out, BRepPrimAPI_MakeBox(gp_Pnt(x0, y0, z0), gp_Pnt(x1, y1, z1)).Shape()); });
}

int iso_make_cylinder(double px, double py, double pz, double dx, double dy, double dz, double r, double h, IsoShape** out)
{
    return guard([&] { return put(out, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(px, py, pz), gp_Dir(dx, dy, dz)), r, h).Shape()); });
}

int iso_make_halfspace(double px, double py, double pz, double nx, double ny, double nz, double size, IsoShape** out)
{
    return guard([&] {
        const gp_Ax2 frame(gp_Pnt(px, py, pz), gp_Dir(nx, ny, nz));
        const gp_Pnt corner = frame.Location().Translated(gp_Vec(frame.XDirection()) * (-size / 2) + gp_Vec(frame.YDirection()) * (-size / 2));
        return put(out, BRepPrimAPI_MakeBox(gp_Ax2(corner, frame.Direction(), frame.XDirection()), size, size, size).Shape());
    });
}

void iso_shape_free(IsoShape* shape) { delete shape; }

int iso_face_count(IsoShape* s, int* count)
{
    *count = s->faces.Extent();
    return 0;
}

int iso_face_info(IsoShape* s, int face, int* kind, double* d)
{
    return guard([&] {
        const TopoDS_Face& f = TopoDS::Face(s->faces(face + 1));
        const BRepAdaptor_Surface surface(f);
        switch (surface.GetType())
        {
        case GeomAbs_Plane: {
            const gp_Pln plane = surface.Plane();
            gp_Dir n = plane.Axis().Direction();
            if (f.Orientation() == TopAbs_REVERSED) n.Reverse();
            const gp_Pnt o = plane.Location();
            *kind = ISO_PLANE;
            d[0] = o.X(); d[1] = o.Y(); d[2] = o.Z();
            d[3] = n.X(); d[4] = n.Y(); d[5] = n.Z();
            return 0;
        }
        case GeomAbs_Cylinder: {
            const gp_Cylinder c = surface.Cylinder();
            *kind = ISO_CYLINDER;
            d[0] = c.Location().X(); d[1] = c.Location().Y(); d[2] = c.Location().Z();
            d[3] = c.Axis().Direction().X(); d[4] = c.Axis().Direction().Y(); d[5] = c.Axis().Direction().Z();
            d[6] = c.Radius();
            return 0;
        }
        case GeomAbs_Torus: {
            const gp_Torus t = surface.Torus();
            *kind = ISO_TORUS;
            d[0] = t.Location().X(); d[1] = t.Location().Y(); d[2] = t.Location().Z();
            d[3] = t.Axis().Direction().X(); d[4] = t.Axis().Direction().Y(); d[5] = t.Axis().Direction().Z();
            d[6] = t.MajorRadius(); d[7] = t.MinorRadius();
            return 0;
        }
        default:
            *kind = ISO_OTHER;
            return 0;
        }
    });
}

int iso_face_distance(IsoShape* s, int face, double x, double y, double z, double* distance)
{
    return guard([&] {
        BRepExtrema_DistShapeShape dist(BRepBuilderAPI_MakeVertex(gp_Pnt(x, y, z)).Vertex(), s->faces(face + 1));
        if (!dist.IsDone()) return fail("distance computation failed");
        *distance = dist.Value();
        return 0;
    });
}

int iso_edge_count(IsoShape* s, int* count)
{
    *count = s->edges.Extent();
    return 0;
}

int iso_edge_faces(IsoShape* s, int edge, int* faces, int capacity, int* count)
{
    return guard([&] {
        const TopoDS_Edge& e = TopoDS::Edge(s->edges(edge + 1));
        if (BRep_Tool::Degenerated(e))
        {
            *count = 0;
            return 0;
        }
        return faceIndices(*s, s->edgeFaces.FindFromKey(e), faces, capacity, count);
    });
}

int iso_edge_vertices(IsoShape* s, int edge, int* first, int* last)
{
    return guard([&] {
        TopoDS_Vertex v1, v2;
        TopExp::Vertices(TopoDS::Edge(s->edges(edge + 1)), v1, v2);
        *first = s->vertices.FindIndex(v1) - 1;
        *last = s->vertices.FindIndex(v2) - 1;
        return 0;
    });
}

int iso_vertex_count(IsoShape* s, int* count)
{
    *count = s->vertices.Extent();
    return 0;
}

int iso_vertex_faces(IsoShape* s, int vertex, int* faces, int capacity, int* count)
{
    return guard([&] { return faceIndices(*s, s->vertexFaces.FindFromKey(s->vertices(vertex + 1)), faces, capacity, count); });
}

int iso_solid_count(IsoShape* s, int* count)
{
    TopTools_IndexedMapOfShape solids;
    TopExp::MapShapes(s->shape, TopAbs_SOLID, solids);
    *count = solids.Extent();
    return 0;
}

int iso_is_valid(IsoShape* s, int* valid)
{
    return guard([&] {
        *valid = BRepCheck_Analyzer(s->shape).IsValid() ? 1 : 0;
        return 0;
    });
}

int iso_volume(IsoShape* s, double* volume)
{
    return guard([&] {
        GProp_GProps props;
        BRepGProp::VolumeProperties(s->shape, props);
        *volume = props.Mass();
        return 0;
    });
}

int iso_write_step(IsoShape* s, const char* path)
{
    return guard([&] {
        STEPControl_Writer writer;
        if (writer.Transfer(s->shape, STEPControl_AsIs) != IFSelect_RetDone) return fail("STEP transfer failed");
        if (writer.Write(path) != IFSelect_RetDone) return fail("STEP write failed");
        return 0;
    });
}

int iso_read_step(const char* path, IsoShape** out)
{
    return guard([&] {
        STEPControl_Reader reader;
        if (reader.ReadFile(path) != IFSelect_RetDone) return fail("STEP read failed");
        if (reader.TransferRoots() == 0) return fail("STEP file has no transferable roots");
        return put(out, reader.OneShape());
    });
}

int iso_vertex_point(IsoShape* s, int vertex, double* xyz)
{
    return guard([&] {
        const gp_Pnt p = BRep_Tool::Pnt(TopoDS::Vertex(s->vertices(vertex + 1)));
        xyz[0] = p.X(); xyz[1] = p.Y(); xyz[2] = p.Z();
        return 0;
    });
}

int iso_edge_point(IsoShape* s, int edge, double t, double* xyz)
{
    return guard([&] {
        const BRepAdaptor_Curve curve(TopoDS::Edge(s->edges(edge + 1)));
        const gp_Pnt p = curve.Value(curve.FirstParameter() + t * (curve.LastParameter() - curve.FirstParameter()));
        xyz[0] = p.X(); xyz[1] = p.Y(); xyz[2] = p.Z();
        return 0;
    });
}

int iso_edge_distance(IsoShape* s, int edge, double x, double y, double z, double* distance)
{
    return guard([&] {
        BRepExtrema_DistShapeShape dist(BRepBuilderAPI_MakeVertex(gp_Pnt(x, y, z)).Vertex(), s->edges(edge + 1));
        if (!dist.IsDone()) return fail("distance computation failed");
        *distance = dist.Value();
        return 0;
    });
}

int iso_max_tolerance(IsoShape* s, double* tolerance)
{
    double t = 0;
    for (int i = 1; i <= s->edges.Extent(); ++i) t = std::max(t, BRep_Tool::Tolerance(TopoDS::Edge(s->edges(i))));
    for (int i = 1; i <= s->vertices.Extent(); ++i) t = std::max(t, BRep_Tool::Tolerance(TopoDS::Vertex(s->vertices(i))));
    *tolerance = t;
    return 0;
}

int iso_tessellate(IsoShape* s, double deflection, double angle, int* triangles)
{
    return guard([&] {
        BRepMesh_IncrementalMesh mesher(s->shape, deflection, Standard_False, angle, Standard_True);
        if (!mesher.IsDone()) return fail("tessellation failed");
        int count = 0;
        for (int i = 1; i <= s->faces.Extent(); ++i)
        {
            TopLoc_Location location;
            const Handle(Poly_Triangulation) t = BRep_Tool::Triangulation(TopoDS::Face(s->faces(i)), location);
            if (!t.IsNull()) count += t->NbTriangles();
        }
        *triangles = count;
        return 0;
    });
}

int iso_boolean(int kind, IsoShape* a, IsoShape* b, int simplify, IsoOp** out)
{
    return guard([&] {
        std::unique_ptr<BRepAlgoAPI_BooleanOperation> bop;
        switch (kind)
        {
        case ISO_FUSE: bop = std::make_unique<BRepAlgoAPI_Fuse>(); break;
        case ISO_CUT: bop = std::make_unique<BRepAlgoAPI_Cut>(); break;
        case ISO_COMMON: bop = std::make_unique<BRepAlgoAPI_Common>(); break;
        default: return fail("unknown boolean kind");
        }
        TopTools_ListOfShape arguments, tools;
        arguments.Append(a->shape);
        tools.Append(b->shape);
        bop->SetArguments(arguments);
        bop->SetTools(tools);
        bop->SetRunParallel(Standard_False); // deterministic results
        bop->SetNonDestructive(Standard_True);
        bop->Build();
        if (!bop->IsDone() || bop->HasErrors()) return fail("boolean operation failed");
        if (simplify) bop->SimplifyResult();

        auto op = std::make_unique<IsoOp>();
        op->args.push_back(std::make_unique<IsoShape>(a->shape));
        op->args.push_back(std::make_unique<IsoShape>(b->shape));
        op->result = std::make_unique<IsoShape>(bop->Shape());
        op->history = bop->History();
        *out = op.release();
        return 0;
    });
}

int iso_fillet(IsoShape* s, const int* edges, int edgeCount, double radius, IsoOp** out)
{
    return guard([&] {
        BRepFilletAPI_MakeFillet fillet(s->shape);
        for (int i = 0; i < edgeCount; ++i) fillet.Add(radius, TopoDS::Edge(s->edges(edges[i] + 1)));
        fillet.Build();
        if (!fillet.IsDone()) return fail("fillet failed");

        TopTools_ListOfShape arguments;
        arguments.Append(s->shape);
        auto op = std::make_unique<IsoOp>();
        op->args.push_back(std::make_unique<IsoShape>(s->shape));
        op->result = std::make_unique<IsoShape>(fillet.Shape());
        op->history = new BRepTools_History(arguments, fillet);
        *out = op.release();
        return 0;
    });
}

int iso_op_result(IsoOp* op, IsoShape** out) { return put(out, op->result->shape); }

int iso_op_image(IsoOp* op, int argument, int face, int* faces, int capacity, int* count)
{
    return guard([&] {
        const TopoDS_Shape& f = op->args.at(argument)->faces(face + 1);
        if (op->history.IsNull()) return fail("operation has no history");
        if (op->history->IsRemoved(f))
        {
            *count = 0;
            return 0;
        }
        const TopTools_ListOfShape& modified = op->history->Modified(f);
        if (!modified.IsEmpty()) return resultIndices(*op, modified, faces, capacity, count);
        TopTools_ListOfShape self;
        self.Append(f);
        return resultIndices(*op, self, faces, capacity, count);
    });
}

int iso_op_generated(IsoOp* op, int argument, int* faces, int capacity, int* count)
{
    return guard([&] {
        if (op->history.IsNull()) return fail("operation has no history");
        const IsoShape& arg = *op->args.at(argument);
        TopTools_ListOfShape generated;
        for (int i = 1; i <= arg.edges.Extent(); ++i)
            for (const TopoDS_Shape& g : op->history->Generated(arg.edges(i))) generated.Append(g);
        for (int i = 1; i <= arg.vertices.Extent(); ++i)
            for (const TopoDS_Shape& g : op->history->Generated(arg.vertices(i))) generated.Append(g);
        return resultIndices(*op, generated, faces, capacity, count);
    });
}

void iso_op_free(IsoOp* op) { delete op; }
