package main

import (
	"encoding/json"
	"os"
	"testing"
)

// TestTraceVectors 逐条回放向量并验证实际值。
//
// 这是一个**自检**：回放器只做向量里说的事（op 是 now 就调 now，是 update
// 就调 update），然后断言结果与向量一致。
//
// 它存在的意义是捕捉"生成器与回放器不一致"这类问题：向量文件既是产物
// 又是期望值，如果只有生成它的那个测试来验证自己，生成器一旦有 bug
// （比如无条件多调一次 now()），错误会被一起固化进向量文件里，永远发现不了。
func TestTraceVectors(t *testing.T) {
	raw, err := os.ReadFile("../test/hlc_vectors.json")
	if err != nil {
		t.Skip("没有向量文件，先跑 TestHLCExportVectors")
	}
	var vf struct {
		Vectors []hlcVector `json:"vectors"`
	}
	if err := json.Unmarshal(raw, &vf); err != nil {
		t.Fatal(err)
	}

	clk := &fixedClock{ms: 0}
	h := NewHLCWithClock(clk.now)

	bad := 0
	for i, v := range vf.Vectors {
		clk.set(v.At)
		var got string
		if v.Op == "update" {
			got = h.Update(v.Remote)
		} else {
			got = h.Now()
		}
		if got != v.Out {
			bad++
			h.mu.Lock()
			l, c := h.l, h.c
			h.mu.Unlock()
			t.Errorf("第 %d 条不一致（op=%s at=%d remote=%q）:\n  向量: %s\n  实际: %s  (l=%d c=%d)",
				i+1, v.Op, v.At, v.Remote, v.Out, got, l, c)
		}
	}
	if bad > 0 {
		t.Errorf("共 %d/%d 条不一致。向量文件与实现已脱节 —— 重新生成："+
			"go test -run TestHLCExportVectors", bad, len(vf.Vectors))
	}
}
