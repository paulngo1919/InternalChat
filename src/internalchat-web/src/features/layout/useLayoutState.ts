import { useState, useEffect } from 'react';

export interface LayoutState {
  isMobile: boolean;
  isTablet: boolean;
  isDesktop: boolean;
}

export function useLayoutState(): LayoutState {
  const [layout, setLayout] = useState<LayoutState>({
    isMobile: window.innerWidth < 768,
    isTablet: window.innerWidth >= 768 && window.innerWidth < 1024,
    isDesktop: window.innerWidth >= 1024,
  });

  useEffect(() => {
    const handleResize = () => {
      const width = window.innerWidth;
      setLayout({
        isMobile: width < 768,
        isTablet: width >= 768 && width < 1024,
        isDesktop: width >= 1024,
      });
    };

    window.addEventListener('resize', handleResize);
    return () => { window.removeEventListener('resize', handleResize); };
  }, []);

  return layout;
}
