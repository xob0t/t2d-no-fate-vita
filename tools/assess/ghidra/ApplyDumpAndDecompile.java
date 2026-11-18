// Headless Ghidra script: label an IL2CPP GameAssembly.dll from flat tables
// (dump_to_tables.py, il2cpp_usages.py, Rewind sig-apply output), create
// functions for a target list plus their direct callees, apply signatures and
// class structs, and write decompiled C + annotated disassembly per target.
//
// args: <tablesDir> <sigApplyTxt> <targets.tsv> <outDir>
//   targets.tsv: id <TAB> fullname-or-0xVA
// @category IL2CPP
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.regex.*;

import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.scalar.Scalar;

public class ApplyDumpAndDecompile extends GhidraScript {
	static class M { long va; String full, ret; boolean stat; String[] params; int shared; }
	static class T { String full, kind, base; int size; List<String[]> fields = new ArrayList<>(); }

	Map<Long, List<M>> methodsByVa = new HashMap<>();
	Map<String, M> methodsByName = new HashMap<>();
	Map<String, T> types = new HashMap<>();
	Map<String, List<String>> typesBySimple = new HashMap<>();
	Map<String, DataType> built = new HashMap<>();
	Map<Long, String> helperKind = new HashMap<>();
	DataTypeManager dtm;
	CategoryPath cat = new CategoryPath("/il2cpp");
	Structure arrayStruct;

	static final Set<String> NORETURN = new HashSet<>(Arrays.asList(
		"Throw", "Rethrow", "ThrowNullReference", "ThrowIndexOutOfRange", "ThrowInvalidCast", "CxxThrow",
		"RaiseProfileException"));

	@Override
	public void run() throws Exception {
		String[] a = getScriptArgs();
		Path tables = Paths.get(a[0]);
		dtm = currentProgram.getDataTypeManager();
		loadTables(tables);
		buildArrayStruct();
		int n = labelMethods();
		println("labelled method addresses: " + n);
		println("usage labels: " + labelUsages(tables.resolve("usages.tsv")));
		println("helper labels: " + labelHelpers(Paths.get(a[1])));
		List<String[]> targets = new ArrayList<>();
		for (String line : Files.readAllLines(Paths.get(a[2]), StandardCharsets.UTF_8)) {
			if (line.isBlank() || line.startsWith("#")) continue;
			targets.add(line.split("\t"));
		}
		Path out = Paths.get(a[3]);
		Files.createDirectories(out);
		DecompInterface dec = new DecompInterface();
		DecompileOptions opts = new DecompileOptions();
		dec.setOptions(opts);
		dec.toggleCCode(true);
		dec.toggleSyntaxTree(true);
		dec.setSimplificationStyle("decompile");
		dec.openProgram(currentProgram);
		for (String[] t : targets) {
			String id = t[0], key = t[1];
			long va;
			M m;
			if (key.startsWith("0x")) { va = Long.parseUnsignedLong(key.substring(2), 16); m = first(va); }
			else { m = methodsByName.get(key); if (m == null) { println("no such method " + key); continue; } va = m.va; }
			Address addr = toAddr(va);
			Function f = ensureFunction(addr);
			if (f == null) { println("could not create function at " + addr); continue; }
			// callees: create, sign, mark noreturn; then rebuild the target so flow respects noreturn
			for (int pass = 0; pass < 2; pass++) {
				Set<Address> callees = new LinkedHashSet<>();
				for (Instruction ins : currentProgram.getListing().getInstructions(f.getBody(), true)) {
					if (!ins.getFlowType().isCall()) continue;
					for (Address d : ins.getFlows()) callees.add(d);
				}
				for (Address c : callees) {
					Function cf = ensureFunction(c);
					if (cf == null) continue;
					applySignature(cf);
				}
				removeFunction(f);
				f = ensureFunction(addr);
			}
			applySignature(f);
			DecompileResults r = dec.decompileFunction(f, 300, monitor);
			String safe = (m != null ? simple(m.full) : "fn_" + Long.toHexString(va)).replaceAll("[^A-Za-z0-9_.-]", "_");
			try (Writer w = Files.newBufferedWriter(out.resolve(id + "-" + safe + ".c"), StandardCharsets.UTF_8)) {
				w.write("// " + (m != null ? m.full : "?") + " @ " + addr + "\n");
				if (r.decompileCompleted()) w.write(r.getDecompiledFunction().getC());
				else w.write("// decompile failed: " + r.getErrorMessage() + "\n");
			}
			try (Writer w = Files.newBufferedWriter(out.resolve(id + "-" + safe + ".asm"), StandardCharsets.UTF_8)) {
				writeListing(f, w);
			}
			println("wrote " + id + " " + safe + " (" + f.getBody().getNumAddresses() + " bytes)");
		}
		dec.dispose();
	}

	M first(long va) { List<M> l = methodsByVa.get(va); return l == null ? null : l.get(0); }

	static String simple(String full) {
		int i = full.indexOf("::");
		String cls = full.substring(0, i), meth = full.substring(i + 2);
		int j = cls.lastIndexOf('.');
		return cls.substring(j + 1) + "." + meth;
	}

	static String clean(String s) {
		s = s.replaceAll("\\s+", "_");
		if (s.length() > 180) s = s.substring(0, 180);
		return s;
	}

	void loadTables(Path dir) throws IOException {
		for (String l : Files.readAllLines(dir.resolve("methods.tsv"), StandardCharsets.UTF_8)) {
			String[] p = l.split("\t", -1);
			M m = new M();
			m.va = Long.parseUnsignedLong(p[0].substring(2), 16);
			m.full = p[1]; m.stat = p[2].equals("1"); m.ret = p[3];
			m.params = p[4].isEmpty() ? new String[0] : p[4].split(";");
			m.shared = Integer.parseInt(p[5]);
			methodsByVa.computeIfAbsent(m.va, k -> new ArrayList<>()).add(m);
			methodsByName.putIfAbsent(m.full, m);
		}
		for (String l : Files.readAllLines(dir.resolve("types.tsv"), StandardCharsets.UTF_8)) {
			String[] p = l.split("\t", -1);
			T t = new T();
			t.full = p[0]; t.kind = p[1]; t.size = p[2].isEmpty() ? 0 : Integer.parseInt(p[2], 16); t.base = p[3];
			types.put(t.full, t);
			int k = t.full.lastIndexOf('.');
			typesBySimple.computeIfAbsent(t.full.substring(k + 1), x -> new ArrayList<>()).add(t.full);
		}
		for (String l : Files.readAllLines(dir.resolve("fields.tsv"), StandardCharsets.UTF_8)) {
			String[] p = l.split("\t", -1);
			T t = types.get(p[0]);
			if (t != null) t.fields.add(p);
		}
	}

	int labelMethods() throws Exception {
		SymbolTable st = currentProgram.getSymbolTable();
		int n = 0;
		for (Map.Entry<Long, List<M>> e : methodsByVa.entrySet()) {
			M m = e.getValue().get(0);
			String name = clean((m.shared > 1 ? "shared" + m.shared + "_" : "") + simple(m.full));
			try { st.createLabel(toAddr(e.getKey()), name, SourceType.USER_DEFINED).setPrimary(); n++; }
			catch (Exception ex) { /* ignore bad names */ }
		}
		return n;
	}

	int labelUsages(Path p) throws Exception {
		if (!Files.exists(p)) return 0;
		SymbolTable st = currentProgram.getSymbolTable();
		int n = 0;
		for (String l : Files.readAllLines(p, StandardCharsets.UTF_8)) {
			String[] q = l.split("\t", -1);
			String name = clean(q[3]).replaceAll("[^\\x21-\\x7e]", "_");
			try { st.createLabel(toAddr(Long.parseUnsignedLong(q[0].substring(2), 16)), name, SourceType.USER_DEFINED); n++; }
			catch (Exception ex) { }
		}
		return n;
	}

	int labelHelpers(Path p) throws Exception {
		SymbolTable st = currentProgram.getSymbolTable();
		Pattern re = Pattern.compile("^\\s+(\\w+)\\s+0x([0-9A-F]+)\\s+\\w+\\s+\\d+ ins -> 0x[0-9A-F]+\\s+(\\S+)");
		int n = 0;
		for (String l : Files.readAllLines(p, StandardCharsets.UTF_8)) {
			Matcher m = re.matcher(l);
			if (!m.find()) continue;
			long va = Long.parseUnsignedLong(m.group(2), 16);
			String mangled = m.group(3);
			String shortName = mangled.startsWith("?") ? mangled.substring(1, Math.max(2, mangled.indexOf('@'))) : mangled;
			helperKind.put(va, m.group(1));
			try { st.createLabel(toAddr(va), clean("rt_" + m.group(1) + "_" + shortName), SourceType.USER_DEFINED).setPrimary(); n++; }
			catch (Exception ex) { }
		}
		// roles reported by Rewind's decompile.log (allocator) are labelled by the caller via targets if needed
		return n;
	}

	Function ensureFunction(Address addr) throws Exception {
		Function f = getFunctionAt(addr);
		if (f != null) return f;
		if (getInstructionAt(addr) == null) {
			DisassembleCommand dc = new DisassembleCommand(addr, null, true);
			dc.applyTo(currentProgram, monitor);
		}
		CreateFunctionCmd cf = new CreateFunctionCmd(addr);
		cf.applyTo(currentProgram, monitor);
		f = getFunctionAt(addr);
		if (f != null) {
			Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(addr);
			if (s != null && s.getSource() == SourceType.USER_DEFINED) {
				try { f.setName(s.getName(), SourceType.USER_DEFINED); } catch (Exception ex) { }
			}
			String hk = helperKind.get(addr.getOffset());
			if (hk != null && NORETURN.contains(hk)) f.setNoReturn(true);
		}
		return f;
	}

	void buildArrayStruct() {
		StructureDataType s = new StructureDataType(cat, "Il2CppArray", 0x20, dtm);
		s.replaceAtOffset(0, PointerDataType.dataType, 8, "klass", null);
		s.replaceAtOffset(8, PointerDataType.dataType, 8, "monitor", null);
		s.replaceAtOffset(0x10, PointerDataType.dataType, 8, "bounds", null);
		s.replaceAtOffset(0x18, UnsignedLongLongDataType.dataType, 8, "max_length", null);
		arrayStruct = (Structure) dtm.addDataType(s, DataTypeConflictHandler.REPLACE_HANDLER);
	}

	static final Map<String, DataType> PRIM = new HashMap<>();
	static {
		PRIM.put("void", VoidDataType.dataType);
		PRIM.put("bool", BooleanDataType.dataType);
		PRIM.put("byte", ByteDataType.dataType);
		PRIM.put("sbyte", SignedByteDataType.dataType);
		PRIM.put("short", ShortDataType.dataType);
		PRIM.put("ushort", UnsignedShortDataType.dataType);
		PRIM.put("char", WideChar16DataType.dataType);
		PRIM.put("int", IntegerDataType.dataType);
		PRIM.put("uint", UnsignedIntegerDataType.dataType);
		PRIM.put("long", LongLongDataType.dataType);
		PRIM.put("ulong", UnsignedLongLongDataType.dataType);
		PRIM.put("float", FloatDataType.dataType);
		PRIM.put("double", DoubleDataType.dataType);
		PRIM.put("System.IntPtr", PointerDataType.dataType);
		PRIM.put("System.UIntPtr", PointerDataType.dataType);
		PRIM.put("nint", PointerDataType.dataType);
		PRIM.put("nuint", PointerDataType.dataType);
	}
	static final Map<String, String> ALIAS = Map.of("string", "System.String", "object", "System.Object",
		"System.Boolean", "bool", "System.Int32", "int", "System.Double", "double", "System.Single", "float",
		"System.Int64", "long", "System.UInt32", "uint", "System.Byte", "byte");

	T resolveType(String name) {
		name = ALIAS.getOrDefault(name, name);
		T t = types.get(name);
		if (t != null) return t;
		List<String> c = typesBySimple.get(name);
		if (c != null && c.size() == 1) return types.get(c.get(0));
		return null;
	}

	/** data type as it appears in a field or a by-value parameter slot */
	DataType slotType(String name) {
		name = name.trim();
		if (name.startsWith("ref ") || name.startsWith("out ") || name.startsWith("in "))
			return new PointerDataType(slotType(name.substring(name.indexOf(' ') + 1)), dtm);
		if (name.endsWith("*")) return PointerDataType.dataType;
		if (name.endsWith("[]")) return new PointerDataType(arrayStruct, dtm);
		String al = ALIAS.getOrDefault(name, name);
		if (PRIM.containsKey(al)) return PRIM.get(al);
		if (name.contains("<") || name.endsWith("?")) return PointerDataType.dataType;
		T t = resolveType(name);
		if (t == null) return PointerDataType.dataType;
		if (t.kind.equals("enum")) {
			for (String[] f : t.fields) if (f[1].equals("value__") && PRIM.containsKey(ALIAS.getOrDefault(f[3], f[3]))) return PRIM.get(ALIAS.getOrDefault(f[3], f[3]));
			return IntegerDataType.dataType;
		}
		if (t.kind.equals("struct")) return valueStruct(t);
		return new PointerDataType(classStruct(t), dtm);
	}

	DataType valueStruct(T t) {
		String key = "V:" + t.full;
		if (built.containsKey(key)) return built.get(key);
		int size = Math.max(1, t.size - 0x10);
		StructureDataType s = new StructureDataType(cat, clean(t.full).replace('.', '_'), size, dtm);
		DataType r = dtm.addDataType(s, DataTypeConflictHandler.REPLACE_HANDLER);
		built.put(key, r);
		fill((Structure) r, t, false);
		return r;
	}

	DataType classStruct(T t) {
		String key = "C:" + t.full;
		if (built.containsKey(key)) return built.get(key);
		int size = Math.max(0x10, t.size);
		StructureDataType s = new StructureDataType(cat, clean(t.full).replace('.', '_') + "_o", size, dtm);
		Structure r = (Structure) dtm.addDataType(s, DataTypeConflictHandler.REPLACE_HANDLER);
		built.put(key, r);
		r.replaceAtOffset(0, PointerDataType.dataType, 8, "klass", null);
		r.replaceAtOffset(8, PointerDataType.dataType, 8, "monitor", null);
		Deque<T> chain = new ArrayDeque<>();
		for (T c = t; c != null; c = (c.base == null || c.base.isEmpty()) ? null : resolveType(c.base)) {
			chain.push(c);
			if (chain.size() > 32) break;
		}
		for (T c : chain) fill(r, c, true);
		return r;
	}

	void fill(Structure s, T t, boolean isClass) {
		for (String[] f : t.fields) {
			if (f[4].equals("1")) continue;
			int off;
			try { off = Integer.parseInt(f[2].substring(2), 16); } catch (Exception e) { continue; }
			DataType dt;
			String ft = f[3];
			if (ft.endsWith("[]")) dt = new PointerDataType(arrayStruct, dtm);
			else {
				String al = ALIAS.getOrDefault(ft, ft);
				T ftt = resolveType(ft);
				if (PRIM.containsKey(al)) dt = PRIM.get(al);
				else if (ftt != null && ftt.kind.equals("struct") && ftt != t) dt = valueStruct(ftt);
				else if (ftt != null && ftt.kind.equals("enum")) dt = slotType(ft);
				else if (ftt != null && !ftt.kind.equals("struct")) dt = new PointerDataType(classStruct(ftt), dtm);
				else dt = PointerDataType.dataType;
			}
			int len = dt.getLength();
			if (off + len > s.getLength()) continue;
			String nm = clean(f[1]).replaceAll("[^A-Za-z0-9_]", "_") + "_" + Integer.toHexString(off);
			try { s.replaceAtOffset(off, dt, len, nm, ft); } catch (Exception e) { }
		}
	}

	void applySignature(Function f) {
		M m = first(f.getEntryPoint().getOffset());
		if (m == null || m.shared > 1) return;
		try {
			List<Variable> ps = new ArrayList<>();
			if (!m.stat) {
				String cls = m.full.substring(0, m.full.indexOf("::"));
				T t = resolveType(cls);
				DataType dt = t == null ? PointerDataType.dataType
					: t.kind.equals("struct") ? new PointerDataType(valueStruct(t), dtm)
					: new PointerDataType(classStruct(t), dtm);
				ps.add(new ParameterImpl("__this", dt, currentProgram));
			}
			int i = 0;
			for (String p : m.params) {
				DataType dt = slotType(p);
				if (dt instanceof Structure && !(dt.getLength() == 1 || dt.getLength() == 2 || dt.getLength() == 4 || dt.getLength() == 8))
					dt = new PointerDataType(dt, dtm);
				ps.add(new ParameterImpl("p" + (i++), dt, currentProgram));
			}
			ps.add(new ParameterImpl("method", PointerDataType.dataType, currentProgram));
			DataType ret = slotType(m.ret);
			f.updateFunction("__fastcall", new ReturnParameterImpl(ret, currentProgram), ps,
				Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS, true, SourceType.USER_DEFINED);
		} catch (Exception e) {
			println("signature failed for " + m.full + ": " + e);
		}
	}

	void writeListing(Function f, Writer w) throws IOException {
		SymbolTable st = currentProgram.getSymbolTable();
		for (Instruction ins : currentProgram.getListing().getInstructions(f.getBody(), true)) {
			Symbol s = st.getPrimarySymbol(ins.getAddress());
			if (s != null && !ins.getAddress().equals(f.getEntryPoint()) && s.getSource() != SourceType.DEFAULT) w.write(s.getName() + ":\n");
			StringBuilder sb = new StringBuilder();
			sb.append(ins.getAddress()).append("  ").append(ins.toString());
			for (Reference r : ins.getReferencesFrom()) {
				Symbol ts = st.getPrimarySymbol(r.getToAddress());
				if (ts != null && ts.getSource() != SourceType.DEFAULT) sb.append("    ; ").append(ts.getName());
			}
			// rip-relative memory operands without a created reference
			for (int op = 0; op < ins.getNumOperands(); op++) {
				for (Object o : ins.getOpObjects(op)) {
					if (o instanceof Address ad && st.getPrimarySymbol(ad) != null && ins.getReferencesFrom().length == 0)
						sb.append("    ; ").append(st.getPrimarySymbol(ad).getName());
				}
			}
			w.write(sb.append('\n').toString());
		}
	}
}
