# Specialization contracts

The synthetic pack and 18 captured cases isolate specialization selection, node
prerequisites, known/unknown and duplicate selections, parent budgets, zero-cost
nodes, formula assumptions and effect ordering between items, sets and passives.
Node authoring order deliberately differs from prerequisite order.

Capture deliberately from the checksum-pinned C0 release:

```powershell
python baseline/capture_resolution.py PATH/TO/character-planner-windows-amd64.zip --suite specialization-resolution
```

The [basic corpus policy](../resolution/README.md) applies: isolated executable,
pack/build inputs, no library commands, complete responses and recorded hashes.
Normal tests consume committed files only. Successful cases compare every stat,
breakdown and ordered contribution; invalid cases compare exact diagnostic fields
and order because the reference CLI withholds calculated results.

Selected known nodes spend their cost once in their budget and each parent even
when prerequisites make them inactive. Overspending reports diagnostics without
disabling otherwise active node effects. Prerequisites determine activation;
the authored tier value does not impose an additional unlock gate. Unknown node
diagnostics precede authored budget diagnostics and authored prerequisite errors.
Focused Core tests cover retained invalid-build calculations and deeper budget
ancestry in addition to the captured responses.
