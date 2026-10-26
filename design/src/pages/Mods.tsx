import { useMemo, useState } from 'react'
import { ArrowUpCircle, Check, Download, RefreshCw, Search, Settings2, SlidersHorizontal } from 'lucide-react'
import { instances } from '../mock'
import { Button, Card, cx, Dot, Tag } from '../ui'

interface Mod {
  id: string
  name: string
  version: string
  loader: string
  enabled: boolean
  update?: string
  size: string
  category: '优化' | '内容' | '库' | '兼容'
}

const mods: Mod[] = [
  { id: 'm1', name: 'fabric-api', version: '0.92.5+1.20.1', loader: 'Fabric', enabled: true, size: '1.8 MB', category: '库' },
  { id: 'm2', name: 'sodium', version: '0.5.11', loader: 'Fabric', enabled: true, update: '0.6.0', size: '812 KB', category: '优化' },
  { id: 'm3', name: 'bettercombat', version: '1.8.6', loader: 'Fabric', enabled: true, update: '1.9.0', size: '1.2 MB', category: '内容' },
  { id: 'm4', name: 'modernfix', version: '5.19.4', loader: 'Fabric', enabled: true, size: '348 KB', category: '优化' },
  { id: 'm5', name: 'alexscaves', version: '1.1.2', loader: 'Fabric', enabled: true, size: '24.6 MB', category: '内容' },
  { id: 'm6', name: 'ferritecore', version: '6.0.1', loader: 'Fabric', enabled: false, size: '96 KB', category: '优化' },
  { id: 'm7', name: 'indium', version: '1.0.34', loader: 'Fabric', enabled: true, size: '512 KB', category: '兼容' },
  { id: 'm8', name: 'patchouli', version: '1.20.1-84', loader: 'Fabric', enabled: true, size: '3.1 MB', category: '库' },
]

const categories = ['全部', '优化', '内容', '库', '兼容'] as const

export default function Mods() {
  const inst = instances[0]
  const [query, setQuery] = useState('')
  const [category, setCategory] = useState<(typeof categories)[number]>('全部')
  const [state, setState] = useState<Record<string, boolean>>(() => Object.fromEntries(mods.map((m) => [m.id, m.enabled])))
  const [onlyUpdatable, setOnlyUpdatable] = useState(false)

  const rows = useMemo(
    () =>
      mods.filter((m) => {
        const byCat = category === '全部' || m.category === category
        const byQuery = !query || m.name.includes(query.toLowerCase())
        const byUpdate = !onlyUpdatable || Boolean(m.update)
        return byCat && byQuery && byUpdate
      }),
    [category, query, onlyUpdatable],
  )

  const enabledCount = Object.values(state).filter(Boolean).length
  const updates = mods.filter((m) => m.update).length

  return (
    <div className="flex h-[calc(100vh-40px-48px)] min-h-[520px] flex-col gap-4">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-[17px] font-semibold tracking-tight text-tx-1">模组</h1>
          <p className="mt-1 text-[11.5px] text-tx-2">
            {inst.name} · 共 <span className="font-mono text-tx-1">{mods.length}</span> 个，已启用{' '}
            <span className="font-mono text-tx-1">{enabledCount}</span> 个
            {updates ? <> · <span className="text-warning">{updates} 个可更新</span></> : null}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="ghost" size="sm" icon={<Download className="h-3.5 w-3.5" />}>从市场安装</Button>
          <Button variant="ghost" size="sm" icon={<RefreshCw className="h-3.5 w-3.5" />}>检查更新</Button>
          <Button variant="primary" size="sm" icon={<ArrowUpCircle className="h-3.5 w-3.5" />}>全部更新</Button>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <div className="flex flex-1 items-center gap-2 rounded-md border border-border bg-surface px-3 py-2">
          <Search className="h-3.5 w-3.5 text-tx-3" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="搜索已安装模组…"
            className="w-full bg-transparent text-[12.5px] text-tx-1 outline-none placeholder:text-tx-3"
          />
        </div>
        <div className="flex items-center gap-1.5">
          {categories.map((c) => (
            <button
              key={c}
              type="button"
              onClick={() => setCategory(c)}
              className={cx(
                'rounded-sm border px-2.5 py-1.5 text-[11.5px] transition-colors duration-150',
                category === c ? 'border-primary/40 bg-primary/10 text-primary' : 'border-border text-tx-3 hover:border-bstrong hover:text-tx-2',
              )}
            >
              {c}
            </button>
          ))}
        </div>
        <button
          type="button"
          onClick={() => setOnlyUpdatable((v) => !v)}
          className={cx(
            'inline-flex items-center gap-1.5 rounded-sm border px-2.5 py-1.5 text-[11.5px] transition-colors duration-150',
            onlyUpdatable ? 'border-warning/40 bg-warning/10 text-warning' : 'border-border text-tx-3 hover:text-tx-2',
          )}
        >
          <SlidersHorizontal className="h-3 w-3" /> 仅可更新
        </button>
      </div>

      <Card className="min-h-0 flex-1 overflow-y-auto p-2">
        <div className="grid grid-cols-[minmax(0,1fr)_120px_92px_84px_64px] items-center gap-3 border-b border-border px-3 pb-2 pt-1 text-[10.5px] text-tx-3">
          <span>名称</span>
          <span>版本</span>
          <span>分类</span>
          <span>大小</span>
          <span className="text-right">启用</span>
        </div>
        {rows.map((m) => {
          const on = state[m.id]
          return (
            <div
              key={m.id}
              className={cx(
                'grid grid-cols-[minmax(0,1fr)_120px_92px_84px_64px] items-center gap-3 rounded-sm px-3 py-2.5 transition-colors duration-150 hover:bg-tint',
                !on && 'opacity-55',
              )}
            >
              <div className="flex min-w-0 items-center gap-2.5">
                <span className="grid h-8 w-8 shrink-0 place-items-center rounded-sm bg-tint font-pixel text-[9px] text-tx-2">
                  {m.name.slice(0, 2).toUpperCase()}
                </span>
                <div className="min-w-0">
                  <div className="flex items-center gap-1.5">
                    <span className="truncate text-[12px] text-tx-1">{m.name}</span>
                    {m.update ? (
                      <span className="inline-flex items-center gap-1 rounded-sm bg-warning/12 px-1.5 py-0.5 text-[10px] text-warning">
                        {m.update} 可用
                      </span>
                    ) : null}
                  </div>
                  <div className="mt-0.5 font-mono text-[10px] text-tx-3">.minecraft/mods/{m.name}.jar</div>
                </div>
              </div>
              <span className="font-mono text-[10.5px] text-tx-2">{m.version}</span>
              <span className="text-[10.5px] text-tx-3">{m.category}</span>
              <span className="font-mono text-[10.5px] text-tx-3">{m.size}</span>
              <span className="flex justify-end">
                <button
                  type="button"
                  onClick={() => setState((prev) => ({ ...prev, [m.id]: !prev[m.id] }))}
                  aria-pressed={on}
                  className={cx(
                    'relative h-4 w-8 rounded-full border transition-colors duration-150',
                    on ? 'border-primary/50 bg-primary/30' : 'border-border bg-tint2',
                  )}
                >
                  <span
                    className={cx(
                      'absolute top-[2px] h-[10px] w-[10px] rounded-full bg-tx-1 transition-all duration-150',
                      on ? 'left-[16px]' : 'left-[2px]',
                    )}
                  />
                </button>
              </span>
            </div>
          )
        })}
        {rows.length === 0 ? (
          <div className="grid h-40 place-items-center text-[11.5px] text-tx-3">没有匹配的模组</div>
        ) : null}
      </Card>

      <div className="flex items-center gap-3 text-[10.5px] text-tx-3">
        <span className="inline-flex items-center gap-1.5"><Dot tone="success" /> 已启用 {enabledCount}</span>
        <span className="inline-flex items-center gap-1.5"><Check className="h-3 w-3" /> 停用的模组不会写进启动参数</span>
        <span className="ml-auto">修改后下次启动生效</span>
      </div>
    </div>
  )
}