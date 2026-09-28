import { LogIn, MessageSquareQuote } from 'lucide-react'

import './LoginScreen.css'

interface LoginScreenProps {
  readonly onSignIn: () => void
}

export function LoginScreen({ onSignIn }: LoginScreenProps) {
  return (
    <div className="login-container">
      <div className="login-background-shapes">
        <div className="shape shape-1"></div>
        <div className="shape shape-2"></div>
        <div className="shape shape-3"></div>
      </div>
      
      <div className="login-card">
        <div className="login-header">
          <div className="login-logo-wrapper">
            <MessageSquareQuote size={36} className="login-logo-icon" />
          </div>
          <h1 className="login-title">Internal Chat</h1>
          <p className="login-subtitle">Connect, collaborate, and build together.</p>
        </div>
        
        <div className="login-body">
          <p className="login-description">
            Sign in to your corporate account to access your workspace and start chatting with your team.
          </p>
          
          <button type="button" className="login-btn-primary" onClick={onSignIn}>
            <LogIn size={20} />
            <span>Sign In to Continue</span>
          </button>
        </div>
        
        <div className="login-footer">
          <p>Secure Enterprise Communication</p>
        </div>
      </div>
    </div>
  )
}
