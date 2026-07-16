import { Link, useNavigate } from '@tanstack/react-router'
import {
  Activity,
  Brain,
  FileJson,
  KeyRound,
  LayoutDashboard,
  LogOut,
  Menu,
  Radio,
  ShieldCheck,
  Server,
} from 'lucide-react'
import { clearSession } from '#/lib/api'
import ThemeToggle from './ThemeToggle'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

const NAV = [
  { to: '/', label: 'Genel bakış', icon: LayoutDashboard },
  { to: '/zones', label: "Zone'lar", icon: ShieldCheck },
  { to: '/schemas', label: 'Şemalar', icon: FileJson },
  { to: '/nodes', label: "Edge node'lar", icon: Server },
  { to: '/certificates', label: 'Sertifikalar', icon: Radio },
  { to: '/live', label: 'Canlı trafik', icon: Activity },
  { to: '/intelligence', label: 'Yapay zeka', icon: Brain },
  { to: '/api-keys', label: 'API anahtarları', icon: KeyRound },
] as const

function Brand() {
  return (
    <Link to="/" className="flex items-center gap-2 px-2 py-1 font-semibold tracking-tight">
      <span className="bg-primary flex size-7 items-center justify-center rounded-md">
        <ShieldCheck className="text-primary-foreground size-4" />
      </span>
      Veil
    </Link>
  )
}

function NavLinks({ onNavigate }: { onNavigate?: () => void }) {
  return (
    <nav className="flex flex-col gap-1">
      {NAV.map((item) => {
        const Icon = item.icon
        return (
          <Link
            key={item.to}
            to={item.to}
            onClick={onNavigate}
            activeOptions={{ exact: item.to === '/' }}
            className="text-sidebar-foreground/70 hover:bg-sidebar-accent hover:text-sidebar-foreground flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors"
            activeProps={{
              className:
                'bg-sidebar-primary/10 text-sidebar-primary hover:bg-sidebar-primary/10 hover:text-sidebar-primary',
            }}
          >
            <Icon className="size-4" />
            {item.label}
          </Link>
        )
      })}
    </nav>
  )
}

function UserMenu() {
  const navigate = useNavigate()
  function logout() {
    clearSession()
    navigate({ to: '/login' })
  }
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="gap-2">
          <span className="bg-muted text-muted-foreground flex size-6 items-center justify-center rounded-full text-xs font-semibold">
            A
          </span>
          admin
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-44">
        <DropdownMenuLabel>admin@veil.local</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={logout} className="text-destructive">
          <LogOut className="size-4" /> Çıkış yap
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

export function AppShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen">
      {/* Sidebar (desktop) */}
      <aside className="bg-sidebar border-sidebar-border sticky top-0 hidden h-screen w-64 shrink-0 flex-col border-r p-3 lg:flex">
        <div className="mb-4">
          <Brand />
        </div>
        <NavLinks />
      </aside>

      {/* Main column */}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="bg-background/80 sticky top-0 z-30 flex h-14 items-center gap-2 border-b px-4 backdrop-blur lg:px-6">
          {/* Mobile nav */}
          <div className="lg:hidden">
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label="Menü">
                  <Menu className="size-5" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="start" className="w-56 p-2">
                <NavLinks />
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
          <div className="lg:hidden">
            <Brand />
          </div>
          <div className="ml-auto flex items-center gap-2">
            <ThemeToggle />
            <UserMenu />
          </div>
        </header>

        <main className="flex-1 p-4 lg:p-8">
          <div className="animate-in fade-in-0 slide-in-from-bottom-1 mx-auto max-w-6xl duration-300">
            {children}
          </div>
        </main>
      </div>
    </div>
  )
}
