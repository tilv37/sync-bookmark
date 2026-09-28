package main

import (
	"strings"
	"testing"
)

func validBookmark(parent string) Item {
	return Item{P: parent, T: TypeBookmark, N: "标题", U: "https://example.com",
		M: Encode(100, 0), A: Encode(100, 0)}
}

func validFolder(parent string) Item {
	return Item{P: parent, T: TypeFolder, N: "目录", M: Encode(100, 0), A: Encode(100, 0)}
}

// setP 修改 map 中某项的父节点。map 的值不可寻址，必须读出—改—写回。
func setP(s *State, key, parent string) {
	it := s.Items[key]
	it.P = parent
	s.Items[key] = it
}

func TestStateValidateAcceptsWellFormed(t *testing.T) {
	s := stateOf(
		kv("f", validFolder(RootToolbar)),
		kv("b", validBookmark(keyOf("f"))),
		kv("top", validBookmark(RootUnfiled)),
	)
	if _, err := s.Validate(); err != nil {
		t.Errorf("合法 state 被拒: %v", err)
	}
}

// 硬错误：会导致解析出错或资源失控的输入必须被拒绝。
func TestStateValidateHardErrors(t *testing.T) {
	cases := []struct {
		name    string
		mutate  func(*State)
		wantSub string
	}{
		{"版本不符", func(s *State) { s.V = 99 }, "schema 版本"},
		{"key 太短", func(s *State) { s.Items["ab"] = validBookmark(RootToolbar) }, "key 非法"},
		{"key 非十六进制", func(s *State) {
			delete(s.Items, keyOf("b"))
			s.Items[strings.Repeat("z", 32)] = validBookmark(RootToolbar)
		}, "key 非法"},
		{"类型非法", func(s *State) {
			it := validBookmark(RootToolbar)
			it.T = "x"
			s.Items[keyOf("b")] = it
		}, "类型非法"},
		{"书签缺 url", func(s *State) {
			it := validBookmark(RootToolbar)
			it.U = ""
			s.Items[keyOf("b")] = it
		}, "必须有 url"},
		{"文件夹带 url", func(s *State) {
			it := validFolder(RootToolbar)
			it.U = "https://x.example"
			s.Items[keyOf("b")] = it
		}, "不应有 url"},
		{"m 非法", func(s *State) {
			it := validBookmark(RootToolbar)
			it.M = "yesterday"
			s.Items[keyOf("b")] = it
		}, "不是合法 HLC"},
		{"a 非法", func(s *State) {
			it := validBookmark(RootToolbar)
			it.A = "???"
			s.Items[keyOf("b")] = it
		}, "不是合法 HLC"},
		{"标题超长", func(s *State) {
			it := validBookmark(RootToolbar)
			it.N = strings.Repeat("x", MaxTitleLen+1)
			s.Items[keyOf("b")] = it
		}, "标题长度"},
		{"url 超长", func(s *State) {
			it := validBookmark(RootToolbar)
			it.U = "https://x.example/" + strings.Repeat("y", MaxURLLen)
			s.Items[keyOf("b")] = it
		}, "url 长度"},
		{"深度超限", func(s *State) {
			s.Items = map[string]Item{}
			parent := RootToolbar
			for i := 0; i <= MaxDepth+2; i++ {
				fk := keyOf("deep" + string(rune('a'+i%26)) + string(rune('a'+i/26)))
				s.Items[fk] = validFolder(parent)
				parent = fk
			}
		}, "深度"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := stateOf(kv("b", validBookmark(RootToolbar)))
			tc.mutate(&s)
			_, err := s.Validate()
			if err == nil {
				t.Fatalf("应当拒绝却通过了")
			}
			if !strings.Contains(err.Error(), tc.wantSub) {
				t.Errorf("错误信息 %q 未包含关键字 %q", err, tc.wantSub)
			}
		})
	}
}

// 软警告：树结构问题只记录不拒绝。
// 让整份上传因为一个孤立节点被拒，对用户来说比跳过那个节点糟糕得多。
func TestStateValidateSoftWarnings(t *testing.T) {
	cases := []struct {
		name     string
		mutate   func(*State)
		wantWarn string
	}{
		{"父节点缺失", func(s *State) {
			setP(s, keyOf("b"), keyOf("ghost"))
		}, "父节点"},
		{"父节点不是文件夹", func(s *State) {
			setP(s, keyOf("b"), keyOf("other"))
		}, "不是文件夹"},
		{"存活项的父节点已删除", func(s *State) {
			it := validFolder(RootToolbar)
			s.Items[keyOf("f")] = tomb(it, t0, 200, 0)
			setP(s, keyOf("b"), keyOf("f"))
		}, "已删除"},
		{"父链成环", func(s *State) {
			a, b := keyOf("cyc-a"), keyOf("cyc-b")
			s.Items[a] = validFolder(b)
			s.Items[b] = validFolder(a)
		}, "异常"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := stateOf(
				kv("b", validBookmark(RootToolbar)),
				kv("other", validBookmark(RootMenu)),
			)
			tc.mutate(&s)
			warns, err := s.Validate()
			if err != nil {
				t.Fatalf("结构问题应记为警告而非硬错误，却返回了: %v", err)
			}
			joined := strings.Join(warns, "; ")
			if !strings.Contains(joined, tc.wantWarn) {
				t.Errorf("警告 %q 未包含关键字 %q", joined, tc.wantWarn)
			}
		})
	}
}

func TestStateValidateWarnsOnTombstoneWithoutTime(t *testing.T) {
	it := validBookmark(RootToolbar)
	it.D = true
	it.X = 0
	s := stateOf(kv("b", it))
	warns, err := s.Validate()
	if err != nil {
		t.Fatalf("不应是硬错误: %v", err)
	}
	if !strings.Contains(strings.Join(warns, ";"), "缺少删除时间") {
		t.Errorf("应提示墓碑缺少删除时间，实际警告: %v", warns)
	}
}

func TestStateValidateEmptyIsFine(t *testing.T) {
	s := NewState()
	if _, err := s.Validate(); err != nil {
		t.Errorf("空 state 应通过校验: %v", err)
	}
}

func TestStateCloneIsDeep(t *testing.T) {
	s := stateOf(kv("b", validBookmark(RootToolbar)))
	c := s.Clone()
	c.Items[keyOf("b")] = validFolder(RootMenu)
	c.Items["new"] = validBookmark(RootUnfiled)

	if len(s.Items) != 1 {
		t.Error("改动 Clone 影响了原 state 的 item 集合")
	}
	if s.Items[keyOf("b")].T != TypeBookmark {
		t.Error("改动 Clone 中的 item 影响了原 state")
	}
}

func TestStateCount(t *testing.T) {
	s := stateOf(
		kv("a", validBookmark(RootToolbar)),
		kv("b", validFolder(RootMenu)),
		kv("c", tomb(validBookmark(RootUnfiled), t0, 200, 0)),
	)
	if got := s.Count(); got != 2 {
		t.Errorf("Count() = %d，期望 2（墓碑不计入）", got)
	}
}

func TestStateKeysSortedIsDeterministic(t *testing.T) {
	s := stateOf()
	for i := 0; i < 50; i++ {
		s.Items[keyOf("k"+string(rune('a'+i%26))+string(rune('a'+i/26)))] = validBookmark(RootToolbar)
	}
	first := s.keysSorted()
	for i := 0; i < 20; i++ {
		got := s.keysSorted()
		if len(got) != len(first) {
			t.Fatalf("长度不一致")
		}
		for j := range got {
			if got[j] != first[j] {
				t.Fatalf("keysSorted 第 %d 次调用结果不同（第 %d 项）", i, j)
			}
		}
	}
}

func TestRootFolderConstants(t *testing.T) {
	for _, id := range []string{RootToolbar, RootMenu, RootUnfiled, RootMobile} {
		if !IsRootFolder(id) {
			t.Errorf("%q 应被识别为根目录", id)
		}
	}
	if IsRootFolder("some-folder-key") {
		t.Error("普通 key 不应被识别为根目录")
	}
	// 根目录 id 长度必须与设计文档一致（Firefox Places 的固定 id）
	for _, id := range []string{RootToolbar, RootMenu, RootUnfiled, RootMobile} {
		if len(id) != 12 {
			t.Errorf("根目录 id %q 长度 %d，期望 12", id, len(id))
		}
	}
}

func TestSameContentIgnoresTimestamps(t *testing.T) {
	a := validBookmark(RootToolbar)
	b := a
	b.M = Encode(999, 9)
	b.A = Encode(888, 8)
	b.D = true
	b.X = 12345

	if !sameContent(a, b) {
		t.Error("sameContent 不应比较 M/A/D/X —— 两端各自重新保存一次不应被当成冲突")
	}

	c := a
	c.N = "别的标题"
	if sameContent(a, c) {
		t.Error("标题不同必须视为内容不同")
	}
}
