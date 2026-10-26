import { useEffect, useMemo, useState } from 'react'
import { ArrowDownToLine, Check, Download as DownloadIcon, Loader2, Search, Tag as TagIcon, X } from 'lucide-react'
import { Button, Card, cx, Dot, Skeleton, Tag } from '../ui'

type Source = 'modrinth' | 'curseforge'
type Kind = '整合包' | '模组' | '光影' | '材质包'

interface Project {
  id: string
  name: string
  author: string
  desc: string
  downloads: string
  versions: string
  loader: string
  kinds: Kind[]
  hue: number
}

const projects: Project[] = [
  { id: 'p1', name: 'Better MC · BMC2', author: 'LunaPixel', desc: '保真原版体验 + 完整任务线与优化，Fabric 端开箱可用。', downloads: '3.7M', versions: '1.20.1', loader: 'Fabric', kinds: ['整合包'], hue: 96 },
  { id: 'p2', name: 'Fabulously Optimized', author: 'robotkoer', desc: '极致帧数与光影兼容的纯客户端整合包。', downloads: '19.4M', versions: '1.21.1', loader: 'Fabric', kinds: ['整合包'], hue: 196 },
  { id: 'p3', name: 'Sodium', author: 'CaffeineMC', desc: '重写渲染管线，帧数翻倍的客户端优化模组。', downloads: '41.2M', versions: '1.16–1.21', loader: 'Fabric', kinds: ['模组'], hue: 168 },
  { id: 'p4', name: 'Complementary Shaders', author: 'EminGT', desc: '均衡向光影，保留原版色彩同时给出体积光与水面反射。', downloads: '24.8M', versions: '1.19–1.21', loader: 'Iris', kinds: ['光影'], hue: 34 },
  { id: 'p5', name: 'Create · 机械动力', author: 'simibubi', desc: '用齿轮与传动轴构建自动化工厂的经典科技模组。', downloads: '12.6M', versions: '1.20.1', loader: 'NeoForge', kinds: ['模组'], hue: 46 },
  { id: 'p6', name: 'Stay True', author: 'FredBob', desc: '高保真原版风格材质包，附 64× 追加包。', downloads: '8.9M', versions: '1.20–1.21', loader: 'Vanilla', kinds: ['材质包'], hue: 268 },
  { id: 'p7', name: 'Cobblemon Official', author: 'CobbledStudios', desc: '把宝可梦带进方块世界，含 1025 只一世代到九世代。', downloads: '11.5M', versions: '1.21.1', loader: 'Fabric', kinds: ['整合包'], hue: 12 },
  { id: 'p8', name: 'FerriteCore', author: 'malte0811', desc: '降低内存占用 20–40%，几乎零兼容性风险。', downloads: '6.3M', versions: '1.16–1.21', loader: 'Forge', kinds: ['模组'], hue: 210 },
]

const kinds: Kind[] = ['整合包', '模组', '光影', '材质包']

export default function Download() {
  const [source, setSource] = useState<Source>('modrinth')
  const [kind, setKind] = useState<Kind>('整合包')
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(false)
  const [active, setActive] = useState<Project | null>(null)

  // 模拟搜索：切换来源/类型/关键词时给一次骨架屏
  useEffect(() => {
    setLoading(true)
    const timer = setTimeout(() => setLoading(false), 420)
    return () => clearTimeout(timer)
  }, [source, kind, query])

  const rows = useMemo(
    () =>
      projects.filter((p) => {
        const byKind = p.kinds.includes(kind)
        const byQuery = !query || (p.name + p.author + p.desc).toLowerCase().includes(query.toLowerCase())
        const bySource = source === 'modrinth' ? p.id !== 'p4' : true
        return byKind && byQuery && bySource
      }),
    [kind, query, source],
  )

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setActive(null)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <div className="flex h-[calc(100vh-40px-48px)] min-h-[520px] flex-col gap-4">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-[17px] font-semibold tracking-tight text-tx-1">资源市场</h1>
          <p className="mt-1 text-[11.5px] text-tx-2">一键安装到当前实例，或作为新版本创建。点击卡片在右侧查看详情。</p>
        </div>
        <div className="flex items-center gap-1 rounded-md border border-border bg-code p-1">
          {(['modrinth', 'curseforge'] as Source[]).map((s) => (
            <button
              key={s}
              type="button"
              onClick={() => setSource(s)}
              className={cx(
                'rounded-sm px-2.5 py-1 text-[11.5px] transition-colors duration-150',
                source === s ? 'bg-tint2 text-tx-1' : 'text-tx-3 hover:text-tx-2',
              )}
            >
              {s === 'modrinth' ? 'Modrinth' : 'CurseForge'}
            </button>
          ))}
        </div>
      </div>

      {/* 搜索 + 类型 */}
      <div className="flex items-center gap-3">
        <div className="flex flex-1 items-center gap-2 rounded-md border border-border bg-surface px-3 py-2">
          <Search className="h-3.5 w-3.5 text-tx-3" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="搜索整合包、模组、光影或材质包…"
            className="w-full bg-transparent text-[12.5px] text-tx-1 outline-none placeholder:text-tx-3"
          />
          {query ? (
            <button type="button" onClick={() => setQuery('')} className="text-tx-3 hover:text-tx-1">
              <X className="h-3.5 w-3.5" />
            </button>
          ) : null}
        </div>
        <div className="flex items-center gap-1.5">
          {kinds.map((k) => (
            <button
              key={k}
              type="button"
              onClick={() => setKind(k)}
              className={cx(
                'rounded-sm border px-2.5 py-1.5 text-[11.5px] transition-colors duration-150',
                kind === k
                  ? 'border-primary/40 bg-primary/10 text-primary'
                  : 'border-border text-tx-3 hover:border-bstrong hover:text-tx-2',
              )}
            >
              {k}
            </button>
          ))}
        </div>
      </div>

      {/* 列表 */}
      <Card className="min-h-0 flex-1 overflow-y-auto p-2">
        {loading ? (
          <div className="space-y-2">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="flex gap-3 rounded-md p-3">
                <Skeleton className="h-14 w-14 shrink-0" />
                <div className="flex-1 space-y-2 py-1">
                  <Skeleton className="h-3.5 w-1/3" />
                  <Skeleton className="h-3 w-2/3" />
                </div>
                <Skeleton className="h-7 w-16 self-center" />
              </div>
            ))}
          </div>
        ) : rows.length === 0 ? (
          <div className="grid h-full place-items-center text-[11.5px] text-tx-3">
            没有匹配的内容，换个关键词或来源试试
          </div>
        ) : (
          <div className="space-y-1">
            {rows.map((p) => (
              <button
                key={p.id}
                type="button"
                onClick={() => setActive(p)}
                className={cx(
                  'flex w-full items-center gap-3 rounded-md px-3 py-2.5 text-left transition-colors duration-150',
                  active?.id === p.id ? 'bg-tint2' : 'hover:bg-tint',
                )}
              >
                <span
                  className="grid h-11 w-11 shrink-0 place-items-center rounded-sm font-pixel text-[10px] text-white/70"
                  style={{ background: `linear-gradient(135deg, hsl(${p.hue} 26% 22%), hsl(${p.hue} 22% 13%))` }}
                >
                  {p.name.slice(0, 2).toUpperCase()}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="flex items-center gap-2">
                    <span className="truncate text-[12.5px] text-tx-1">{p.name}</span>
                    <span className="shrink-0 text-[10.5px] text-tx-3">{p.author}</span>
                  </span>
                  <span className="mt-0.5 line-clamp-1 text-[11px] text-tx-3">{p.desc}</span>
                </span>
                <span className="hidden shrink-0 items-center gap-3 font-mono text-[10.5px] text-tx-3 lg:flex">
                  <span>{p.versions}</span>
                  <span>{p.downloads} 次下载</span>
                </span>
                <Tag tone={p.loader}>{p.loader}</Tag>
                <span className="grid h-7 w-7 shrink-0 place-items-center rounded-sm text-tx-3">
                  <DownloadIcon className="h-3.5 w-3.5" />
                </span>
              </button>
            ))}
          </div>
        )}
      </Card>

      <div className="flex items-center gap-3 text-[10.5px] text-tx-3">
        <span className="inline-flex items-center gap-1.5"><Dot tone="success" /> 来源 {source === 'modrinth' ? 'Modrinth（无需 Key）' : 'CurseForge（需 API Key）'}</span>
        <span>共 {rows.length} 条结果</span>
        <span className="ml-auto">点击卡片 → 右侧详情抽屉（Esc 关闭）</span>
      </div>

      {/* 右侧详情抽屉 */}
      {active ? (
        <div className="fixed inset-0 z-30 flex justify-end bg-scrim backdrop-blur-[2px]" onClick={() => setActive(null)}>
          <aside
            className="flex h-full w-[420px] flex-col border-l border-border bg-elevated shadow-soft"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-start justify-between border-b border-border p-4">
              <div className="flex items-center gap-3">
                <span
                  className="grid h-12 w-12 place-items-center rounded-sm font-pixel text-[11px] text-white/70"
                  style={{ background: `linear-gradient(135deg, hsl(${active.hue} 26% 22%), hsl(${active.hue} 22% 13%))` }}
                >
                  {active.name.slice(0, 2).toUpperCase()}
                </span>
                <div>
                  <div className="text-[13.5px] font-medium text-tx-1">{active.name}</div>
                  <div className="mt-0.5 text-[10.5px] text-tx-3">{active.author} · {active.downloads} 次下载</div>
                </div>
              </div>
              <button type="button" onClick={() => setActive(null)} className="text-tx-3 hover:text-tx-1" aria-label="关闭">
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
              <p className="text-[11.5px] leading-relaxed text-tx-2">{active.desc}</p>

              <div className="flex flex-wrap gap-1.5">
                {active.kinds.map((k) => (
                  <span key={k} className="inline-flex items-center gap-1 rounded-sm border border-border px-1.5 py-0.5 text-[10.5px] text-tx-2">
                    <TagIcon className="h-2.5 w-2.5" />{k}
                  </span>
                ))}
                <Tag tone={active.loader}>{active.loader}</Tag>
              </div>

              <div>
                <div className="mb-2 text-[11px] text-tx-3">可用版本</div>
                <div className="space-y-1.5">
                  {[active.versions, '1.20.1', '1.19.2'].slice(0, 3).map((v, i) => (
                    <div key={v + i} className="flex items-center justify-between rounded-sm border border-border px-2.5 py-2">
                      <div>
                        <div className="font-mono text-[11px] text-tx-1">MC {v}</div>
                        <div className="mt-0.5 text-[10px] text-tx-3">{i === 0 ? '最新发布' : '历史版本'}</div>
                      </div>
                      <Button variant={i === 0 ? 'primary' : 'ghost'} size="sm" icon={<ArrowDownToLine className="h-3 w-3" />}>
                        安装
                      </Button>
                    </div>
                  ))}
                </div>
              </div>

              {loading ? (
                <div className="flex items-center gap-2 text-[10.5px] text-tx-3"><Loader2 className="h-3 w-3 animate-spin" /> 正在读取版本…</div>
              ) : (
                <div className="flex items-center gap-1.5 text-[10.5px] text-tx-3">
                  <Check className="h-3 w-3 text-success" /> 已校验来源与加载器兼容性
                </div>
              )}
            </div>

            <div className="border-t border-border p-4">
              <Button variant="primary" className="w-full" icon={<DownloadIcon className="h-4 w-4" />}>
                安装到当前实例
              </Button>
              <p className="mt-2 text-center text-[10px] text-tx-3">不会修改已有实例的存档与配置</p>
            </div>
          </aside>
        </div>
      ) : null}
    </div>
  )
}